using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LlamaHarness;

/// <summary>
/// 输出续接 + 崩溃恢复管道：
/// - 流式原位续接：finish_reason=length → 追加 assistant 输出 + 续接指令发起下一轮，继续灌入同一条流（客户端无感）。
///   末块 finish_reason 归一化为 stop + usage 跨轮累加。
/// - 工具隔离：累积输出中出现 tool_calls → 不续接（透传），防 tool_call JSON 拼接损坏。
/// - 崩溃识别：流在无 [DONE] 时中断 → 返回 Aborted（调用方判定 bad_alloc 并触发恢复）。
/// - 非流式：循环续接；bad_alloc 错误响应 → 返回未完成（不转发错误体，交给崩溃恢复）。
/// </summary>
public static class OutputContinuer
{
    private const string ContinuePrompt = "请继续输出，不要重复已有内容，延续上文逻辑完成剩余内容";

    /// <summary>单轮 SSE 管道结果。</summary>
    private enum RoundOutcome { Normal, Truncated, Aborted }

    /// <summary>跨轮累计状态。</summary>
    private sealed class SseState
    {
        public StringBuilder Accumulated { get; } = new(); // 累计生成内容（续接回填用）
        public bool HasToolCalls { get; set; }             // 输出出现 tool_calls → 不续接
        public string? FinishReason { get; set; }          // 本轮末块 finish_reason
        public long PromptTokens { get; set; }             // usage 跨轮累加
        public long CompletionTokens { get; set; }
        public bool HasUsage { get; set; }
    }

    /// <summary>流式原位续接：把 firstResp 的 SSE 灌入客户端；finish_reason=length 时自动续接（最多 cfg.MaxContinuations 轮）。</summary>
    /// <param name="onTruncation">截断断点回调：finish_reason=length 触发、续接请求发出前调用（槽位 KV 仍完整，可 save 断点快照）。null = 不启用。</param>
    /// <returns>(Completed, Accumulated)：Completed=false 表示流中断（需崩溃恢复）；Accumulated = 已生成内容。</returns>
    public static Task<(bool Completed, string Accumulated)> HandleStreamAsync(
        HttpClient hc, Uri uri, int backendPort, string originalBody,
        HttpResponseMessage firstResp, HttpListenerResponse outResp,
        AppConfig cfg, Action<string>? log, Func<Task>? onTruncation = null)
        => PipeLoop(hc, uri, backendPort, originalBody, firstResp, outResp, cfg, log, onTruncation: onTruncation);

    /// <summary>发起新的推理请求并把 SSE 灌入客户端（崩溃恢复重放路径；同样支持截断续接）。</summary>
    /// <param name="writeGate">写门控：与并发 keep-alive 写入互斥，防 SSE 行交错损坏。null = 无并发写者（普通路径）。</param>
    public static async Task<(bool Completed, string Accumulated)> SendAndPipeStreamAsync(
        HttpClient hc, Uri uri, int backendPort, string body,
        HttpListenerResponse outResp, AppConfig cfg, Action<string>? log,
        SemaphoreSlim? writeGate = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(cfg.ContinuationTimeoutSeconds));
        var resp = await hc.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        return await PipeLoop(hc, uri, backendPort, body, resp, outResp, cfg, log, writeGate);
    }

    /// <summary>核心管道循环：灌一轮 SSE；截断时自动续接（最多 MaxContinuations 轮）。</summary>
    private static async Task<(bool Completed, string Accumulated)> PipeLoop(
        HttpClient hc, Uri uri, int backendPort, string originalBody,
        HttpResponseMessage firstResp, HttpListenerResponse outResp,
        AppConfig cfg, Action<string>? log, SemaphoreSlim? writeGate = null,
        Func<Task>? onTruncation = null)
    {
        var state = new SseState();
        HttpResponseMessage resp = firstResp;
        int round = 0;

        while (true)
        {
            bool allowContinue = cfg.ContinuationEnabled && round < cfg.MaxContinuations;
            var outcome = await PipeOneRoundAsync(resp, outResp, state, allowContinue, writeGate);
            resp.Dispose();
            if (outcome != RoundOutcome.Truncated)
                return (outcome != RoundOutcome.Aborted, state.Accumulated.ToString());

            // P1：跨轮 keep-alive——等待下一轮期间（KV save / tokenize / prefill）周期写 SSE 注释行，
            // 防客户端空闲超时掐线；本轮响应到手后取消并等最后一条注释写完再开下一轮管道，防 SSE 行交错
            using var kaCts = new CancellationTokenSource();
            var keepAlive = RunKeepAlive(outResp, writeGate, kaCts.Token);
            try
            {
                // 截断断点回调（§4.1）：续接请求发出前触发，槽位 KV 仍完整（可 save 断点快照）；失败不阻断续接
                if (onTruncation != null)
                {
                    try { await onTruncation(); } catch { /* 断点快照失败不影响续接 */ }
                }

                // 构造续接请求：追加 assistant 输出 + 续接指令（originalBody 含 n_slots → 同槽亲和，KV 前缀命中免重算）
                var nextBody = BuildContinuationBody(originalBody, state.Accumulated.ToString());
                if (nextBody == null) return (false, state.Accumulated.ToString());
                originalBody = nextBody;

                // TokenGuard 防护（续接输入可能超预算）
                int budget = cfg.GetInputBudget();
                var (ok, guarded, note) = await TokenGuard.GuardAsync(hc, backendPort, originalBody, budget);
                if (!ok) { log?.Invoke($"续接中止：{note}"); return (false, state.Accumulated.ToString()); }
                if (guarded != null && guarded != originalBody) originalBody = guarded;
                if (note != null) log?.Invoke(note);

                // 发起下一轮推理
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Post, uri)
                    {
                        Content = new StringContent(originalBody, Encoding.UTF8, "application/json"),
                    };
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(cfg.ContinuationTimeoutSeconds));
                    resp = await hc.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                    round++;
                    log?.Invoke($"续接触发（第 {round} 轮）：输出截断（finish_reason=length），自动续接…");
                }
                catch (Exception ex)
                {
                    log?.Invoke($"续接请求异常（第 {round + 1} 轮）：{ex.Message}");
                    return (false, state.Accumulated.ToString());
                }
            }
            finally
            {
                kaCts.Cancel();
                await keepAlive; // 等最后一条注释行写完再开下一轮管道
            }
        }
    }

    /// <summary>P1：跨轮 keep-alive——等待下一轮期间周期写 SSE 注释行（客户端按规范忽略），防空闲超时掐线。</summary>
    private static Task RunKeepAlive(HttpListenerResponse outResp, SemaphoreSlim? writeGate, CancellationToken ct)
    {
        return Task.Run(async () =>
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(2000, ct);
                    var bytes = Encoding.UTF8.GetBytes(": 续接中…\n");
                    if (writeGate != null) await writeGate.WaitAsync();
                    try
                    {
                        await outResp.OutputStream.WriteAsync(bytes);
                        await outResp.OutputStream.FlushAsync();
                    }
                    finally
                    {
                        if (writeGate != null) writeGate.Release();
                    }
                }
            }
            catch (OperationCanceledException) { /* 正常取消 */ }
            catch { /* 客户端已断开，keep-alive 失败不影响主流程 */ }
        });
    }

    /// <summary>
    /// 灌一轮 SSE 到客户端并累积内容；末块（含 finish_reason）暂扣待决策/改写。
    /// Normal = 正常结束；Truncated = 需续接（末块已剥离 finish_reason、本轮 [DONE] 抑制不写，客户端继续等待下一轮流）；
    /// Aborted = 流在无 [DONE] 时中断（崩溃迹象）。
    /// </summary>
    private static async Task<RoundOutcome> PipeOneRoundAsync(
        HttpResponseMessage resp, HttpListenerResponse outResp, SseState state, bool allowContinue,
        SemaphoreSlim? writeGate = null)
    {
        var stream = resp.Content.ReadAsStream();
        var pending = new List<byte>(65536);
        var held = new List<string>();      // finish_reason 之后暂扣的原始行（含 [DONE]）
        string? finalPayload = null;        // 含 finish_reason 的最后 chunk JSON
        string? finalReason = null;
        bool holding = false;
        bool sawDone = false;
        var chunk = new byte[8192];

        // 单遍扫描：只从上次扫描位置继续找换行，行处理完批量移除头部（原实现每找到一行就 RemoveRange+从头重扫，O(n²)）
        int lineStart = 0;   // 当前未处理行的起始下标
        int scanFrom = 0;    // 下一轮扫描起点（上次扫描到的位置）
        while (true)
        {
            int n = await stream.ReadAsync(chunk);
            if (n <= 0) break;
            for (int j = 0; j < n; j++) pending.Add(chunk[j]);
            for (int i = scanFrom; i < pending.Count; i++)
            {
                if (pending[i] != (byte)'\n') continue;
                var line = DecodeLine(pending, lineStart, i);
                await HandleSseLineAsync(line);
                lineStart = i + 1;
            }
            scanFrom = pending.Count; // 已扫到末尾，下轮从新追加的字节继续
            if (lineStart > 0)
            {
                pending.RemoveRange(0, lineStart); // 批量移除已处理字节，未完整行保留在头部
                lineStart = 0;
                scanFrom = pending.Count;
            }
        }
        if (pending.Count > 0)
            await HandleSseLineAsync(DecodeLine(pending, 0, pending.Count));

        /// <summary>写一行到客户端；有门控时先取锁（与并发 keep-alive 互斥，防 SSE 行交错）。</summary>
        async Task ForwardAsync(string line)
        {
            var bytes = Encoding.UTF8.GetBytes(line + "\n");
            if (writeGate != null) await writeGate.WaitAsync();
            try
            {
                await outResp.OutputStream.WriteAsync(bytes);
                await outResp.OutputStream.FlushAsync();
            }
            finally
            {
                if (writeGate != null) writeGate.Release();
            }
        }

        async Task HandleSseLineAsync(string line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (holding) held.Add(line + "\n"); else await ForwardAsync(line);
                return;
            }
            var payload = line.Substring(5).Trim();
            if (payload == "[DONE]")
            {
                sawDone = true;
                if (holding) held.Add(line + "\n"); else await ForwardAsync(line);
                return;
            }
            JsonObject? obj = null;
            try { obj = JsonNode.Parse(payload)?.AsObject(); } catch { }
            if (obj != null)
            {
                var choice = (obj["choices"] as JsonArray)?.FirstOrDefault()?.AsObject();
                var delta = choice?["delta"]?.AsObject();
                var content = delta?["content"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(content)) state.Accumulated.Append(content);
                if (delta?["tool_calls"] != null) state.HasToolCalls = true;
                var fr = choice?["finish_reason"]?.GetValue<string>();
                if (fr != null)
                {
                    holding = true;
                    finalPayload = payload;
                    finalReason = fr;
                    return; // 暂扣：本轮结束后决策
                }
                var usage = obj["usage"];
                if (usage != null)
                {
                    try
                    {
                        state.PromptTokens += usage["prompt_tokens"]?.GetValue<int>() ?? 0;
                        state.CompletionTokens += usage["completion_tokens"]?.GetValue<int>() ?? 0;
                        state.HasUsage = true;
                    }
                    catch { }
                }
            }
            await ForwardAsync(line);
        }

        // ── 流结束决策 ──
        if (holding && finalPayload != null)
        {
            bool doContinue = finalReason == "length" && allowContinue && !state.HasToolCalls;
            var finalObj = JsonNode.Parse(finalPayload)?.AsObject();
            if (finalObj != null)
            {
                var ch = (finalObj["choices"] as JsonArray)?.FirstOrDefault()?.AsObject();
                if (doContinue)
                {
                    // 剥离 finish_reason：客户端继续等待下一轮流
                    if (ch != null) ch["finish_reason"] = JsonValue.Create<string>(null);
                }
                else
                {
                    // 归一化：强制 stop + 合并跨轮 usage
                    if (ch != null) ch["finish_reason"] = "stop";
                    if (state.HasUsage)
                    {
                        finalObj["usage"] = new JsonObject
                        {
                            ["prompt_tokens"] = state.PromptTokens,
                            ["completion_tokens"] = state.CompletionTokens,
                            ["total_tokens"] = state.PromptTokens + state.CompletionTokens,
                        };
                    }
                }
                finalPayload = finalObj.ToJsonString();
            }
            await ForwardAsync("data: " + finalPayload);
            // P0：暂扣行（含 [DONE]）只在真正末轮写出——续接分支若泄漏 [DONE]，客户端会判定流结束而断开连接，
            // 后续轮输出永远不可见，续接链路被毁。续接时丢弃本轮 [DONE]，留给真正末轮。
            if (!doContinue)
            {
                // 整体持锁写入，防 keep-alive 在末块与 [DONE] 之间插入
                if (writeGate != null) await writeGate.WaitAsync();
                try
                {
                    foreach (var h in held)
                    {
                        var bytes = Encoding.UTF8.GetBytes(h);
                        await outResp.OutputStream.WriteAsync(bytes);
                    }
                    await outResp.OutputStream.FlushAsync();
                }
                finally
                {
                    if (writeGate != null) writeGate.Release();
                }
            }
            return doContinue ? RoundOutcome.Truncated : RoundOutcome.Normal;
        }

        // 无 finish_reason chunk：见过 [DONE] = 正常结束；否则 = 流中断（崩溃迹象）
        return sawDone ? RoundOutcome.Normal : RoundOutcome.Aborted;
    }

    /// <summary>非流式续接：读完整 JSON 响应；finish_reason=length 时循环续接；末轮归一化 finish_reason=stop + 合并 usage。</summary>
    /// <returns>(Completed, Accumulated)：Completed=false 表示 bad_alloc 错误（恢复启用时不转发错误体，交给崩溃恢复）。</returns>
    public static async Task<(bool Completed, string Accumulated)> HandleNonStreamAsync(
        HttpClient hc, Uri uri, int backendPort, string originalBody,
        HttpResponseMessage firstResp, HttpListenerResponse outResp,
        AppConfig cfg, Action<string>? log, bool crashRecoveryEnabled)
    {
        var state = new SseState();
        string body = Encoding.UTF8.GetString(await firstResp.Content.ReadAsByteArrayAsync());
        int round = 0;

        // bad_alloc 错误响应：恢复启用 → 不转发，交给崩溃恢复；否则原样透传
        if (firstResp.StatusCode >= System.Net.HttpStatusCode.InternalServerError
            && (body.Contains("bad allocation", StringComparison.OrdinalIgnoreCase)
                || CrashRecovery.WasBadAlloc(TimeSpan.FromSeconds(60))))
        {
            if (crashRecoveryEnabled) return (false, "");
            await WriteJsonToClient(outResp, body);
            return (true, "");
        }

        while (true)
        {
            bool allowContinue = cfg.ContinuationEnabled && round < cfg.MaxContinuations;
            if (!ParseNonStream(body, state)) break; // 解析失败：原样转发
            if (state.FinishReason != "length" || state.HasToolCalls || !allowContinue) break;

            var nextBody = BuildContinuationBody(originalBody, state.Accumulated.ToString());
            if (nextBody == null) break;
            originalBody = nextBody;

            int budget = cfg.GetInputBudget();
            var (ok, guarded, note) = await TokenGuard.GuardAsync(hc, backendPort, originalBody, budget);
            if (!ok) { log?.Invoke($"续接中止：{note}"); break; }
            if (guarded != null && guarded != originalBody) originalBody = guarded;
            if (note != null) log?.Invoke(note);

            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, uri)
                {
                    Content = new StringContent(originalBody, Encoding.UTF8, "application/json"),
                };
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(cfg.ContinuationTimeoutSeconds));
                using var r2 = await hc.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                body = Encoding.UTF8.GetString(await r2.Content.ReadAsByteArrayAsync(cts.Token));
                round++;
                log?.Invoke($"续接触发（第 {round} 轮）：输出截断，自动续接…");
            }
            catch (Exception ex)
            {
                log?.Invoke($"续接请求异常（第 {round + 1} 轮）：{ex.Message}，返回已生成内容。");
                break;
            }
        }

        // 归一化：finish_reason=stop + 合并 usage
        try
        {
            var root = JsonNode.Parse(body)?.AsObject();
            if (root != null)
            {
                var ch = (root["choices"] as JsonArray)?.FirstOrDefault()?.AsObject();
                if (ch != null) ch["finish_reason"] = "stop";
                if (state.HasUsage)
                {
                    root["usage"] = new JsonObject
                    {
                        ["prompt_tokens"] = state.PromptTokens,
                        ["completion_tokens"] = state.CompletionTokens,
                        ["total_tokens"] = state.PromptTokens + state.CompletionTokens,
                    };
                }
                body = root.ToJsonString();
            }
        }
        catch { /* 改写失败：原样转发 */ }

        await WriteJsonToClient(outResp, body);
        return (true, state.Accumulated.ToString());
    }

    /// <summary>写 JSON 响应到客户端。</summary>
    private static async Task WriteJsonToClient(HttpListenerResponse outResp, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        outResp.ContentType = "application/json";
        outResp.ContentLength64 = bytes.Length;
        await outResp.OutputStream.WriteAsync(bytes);
    }

    /// <summary>解析非流式响应：累积 content/usage/finish_reason。解析失败返回 false。</summary>
    private static bool ParseNonStream(string body, SseState state)
    {
        try
        {
            var root = JsonNode.Parse(body)?.AsObject();
            if (root == null) return false;
            var ch = (root["choices"] as JsonArray)?.FirstOrDefault()?.AsObject();
            if (ch != null)
            {
                var msg = ch["message"]?.AsObject();
                var content = msg?["content"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(content)) state.Accumulated.Append(content);
                if (msg?["tool_calls"] != null) state.HasToolCalls = true;
                state.FinishReason = ch["finish_reason"]?.GetValue<string>();
            }
            var usage = root["usage"];
            if (usage != null)
            {
                state.PromptTokens += usage["prompt_tokens"]?.GetValue<int>() ?? 0;
                state.CompletionTokens += usage["completion_tokens"]?.GetValue<int>() ?? 0;
                state.HasUsage = true;
            }
            return true;
        }
        catch { return false; }
    }

    /// <summary>在原请求 messages 末尾追加 assistant 输出 + 续接指令。失败返回 null。</summary>
    public static string? BuildContinuationBody(string originalBody, string content)
    {
        try
        {
            var root = JsonNode.Parse(originalBody)?.AsObject();
            var msgs = root?["messages"] as JsonArray;
            if (root == null || msgs == null) return null;
            msgs.Add(new JsonObject { ["role"] = "assistant", ["content"] = content });
            msgs.Add(new JsonObject { ["role"] = "user", ["content"] = ContinuePrompt });
            return root.ToJsonString();
        }
        catch { return null; }
    }

    /// <summary>从字节列表取 [start, end) 区间解码为一行（去 \r）。</summary>
    private static string DecodeLine(List<byte> pending, int start, int end)
    {
        var bytes = new byte[end - start];
        for (int j = 0; j < bytes.Length; j++) bytes[j] = pending[start + j];
        var s = Encoding.UTF8.GetString(bytes);
        if (s.EndsWith("\r")) s = s.Substring(0, s.Length - 1);
        return s;
    }
}
