using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LlamaHarness;

/// <summary>
/// Token Guard：代理层 token 预估算 + 裁剪，防 "request exceeds context size" 400 错误。
/// - 计数：POST /v1/tokenize 到后端 llama-server（真实分词器，本地毫秒级）
/// - 预算：CtxSize ÷ Parallel − ReservedOutputTokens（多槽均分总容量）
/// - 裁剪：轮次制（整轮删除最旧对话，保证 tool_call/tool_result 配对完整）
///   + 内容兜底（单条超大消息如巨型 tool_result 做字符级截断）
/// - 降级：tokenize 失败 → 原样转发不阻断；无 user 消息 → 透传
/// </summary>
public static class TokenGuard
{
    /// <summary>经后端 /v1/tokenize 端点计数 token。失败返回 null（调用方降级原样转发）。</summary>
    public static async Task<int?> CountTokensAsync(HttpClient hc, int port, string text)
    {
        try
        {
            var payload = new JsonObject { ["content"] = text };
            using var req = new HttpRequestMessage(HttpMethod.Post, $"http://localhost:{port}/v1/tokenize")
            {
                Content = new StringContent(payload.ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
            };
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var resp = await hc.SendAsync(req, cts.Token);
            if (!resp.IsSuccessStatusCode) return null;
            var body = await resp.Content.ReadAsStringAsync(cts.Token);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            // 兼容两种响应格式：{"tokens":[...]}（数数组长度）/ {"n_tokens":N}
            if (root.TryGetProperty("tokens", out var toks) && toks.ValueKind == JsonValueKind.Array)
                return toks.GetArrayLength();
            if (root.TryGetProperty("n_tokens", out var n) && n.TryGetInt32(out var v))
                return v;
            return null;
        }
        catch
        {
            return null; // 后端忙 / 超时：降级
        }
    }

    /// <summary>
    /// 主入口：检查 + 裁剪 chat/completions 请求体。
    /// 预算内 → (true, 原body, null)；裁剪成功 → (true, 新body, 日志说明)；
    /// 最小集仍超预算 → (false, null, 错误信息)，调用方返回 400。
    /// </summary>
    public static async Task<(bool Ok, string? Body, string? Note)> GuardAsync(
        HttpClient hc, int backendPort, string body, int budget)
    {
        // 解析 body 提取 messages（非 JSON / 无 messages → 透传）
        JsonObject root;
        JsonArray? messages;
        try
        {
            root = JsonNode.Parse(body)?.AsObject() ?? throw new InvalidOperationException();
            messages = root["messages"] as JsonArray;
        }
        catch
        {
            return (true, body, null);
        }
        if (messages == null || messages.Count == 0) return (true, body, null);

        // O-14：计数口径对齐——送 messages 文本（role+content）tokenize，而非原始 body（含 model/temperature 等非上下文字段，计数偏高）
        int count = await CountTokensAsync(hc, backendPort, BuildMessagesText(messages)) ?? -1;
        if (count < 0) return (true, body, null); // tokenize 失败：降级原样转发
        int origCount = count;
        if (count <= budget) return (true, body, null);

        // ── 轮次制裁剪 ──
        // 一轮 = user 消息 + 其后到下一个 user 之前的 assistant/tool 消息（整体删除，保 tool_call 配对）。
        // 最小保留集：首个 user 之前的全部消息（system 等）+ 最后一轮（最后 user → 末尾）。
        int firstUser = FirstIndexOfRole(messages, "user");
        int lastUser = LastIndexOfRole(messages, "user");
        if (firstUser < 0) return (true, body, null); // 无 user 消息：无可裁

        var turnStarts = new List<int>();
        for (int i = firstUser; i <= lastUser; i++)
            if (RoleOf(messages[i]) == "user") turnStarts.Add(i);

        int deletedTurns = 0;
        while (count > budget && turnStarts.Count > 1)
        {
            int start = turnStarts[0];
            int end = turnStarts[1]; // 下一轮起点（不含）
            for (int i = end - 1; i >= start; i--) messages.RemoveAt(i);
            turnStarts.RemoveAt(0);
            deletedTurns++;
            body = root.ToJsonString();
            count = await CountTokensAsync(hc, backendPort, BuildMessagesText(messages)) ?? -1;
            if (count < 0) return (true, body, null); // 中途降级：用当前状态
        }

        // ── 内容兜底 ── 最小集仍超 → 截断最大消息内容（巨型 tool_result 等）
        if (count > budget)
        {
            for (int iter = 0; iter < 10 && count > budget; iter++)
            {
                int maxIdx = IndexOfLargestContent(messages);
                string? content = maxIdx >= 0 ? GetContent(messages[maxIdx]) : null;
                if (content == null || content.Length < 200) break; // 无可再裁的内容
                double ratio = Math.Max(0.1, 1.0 - (count - (double)budget) / count);
                int newLen = Math.Max(50, (int)(content.Length * ratio));
                // O-14：头尾双保留（头部留上下文、尾部留最新信息），替代纯头部截断
                int half = newLen / 2;
                int tail = newLen - half;
                string kept = content[..half] + "\n[…]\n" + content[^tail..];
                SetContent(messages[maxIdx], kept + "\n[已截断 - Token Guard]");
                body = root.ToJsonString();
                count = await CountTokensAsync(hc, backendPort, BuildMessagesText(messages)) ?? -1;
                if (count < 0) return (true, body, null);
            }
        }

        if (count > budget)
        {
            var err = $"Token Guard：裁剪后仍 {count} tokens，超预算 {budget}。请缩短输入。";
            return (false, null, err);
        }

        var note = $"Token Guard：估算 {origCount} tokens > 预算 {budget}，删除 {deletedTurns} 轮对话，最终 {count} tokens";
        return (true, body, note);
    }

    // ── 辅助 ──

    /// <summary>O-14：构造送 tokenize 的计数文本——逐条拼接 role + content（与上下文消耗口径对齐）。</summary>
    private static string BuildMessagesText(JsonArray messages)
    {
        var sb = new StringBuilder();
        foreach (var m in messages)
        {
            var o = m?.AsObject();
            if (o == null) continue;
            var role = o["role"]?.GetValue<string>() ?? "";
            var c = o["content"];
            string? content = null;
            try { content = c?.GetValue<string>(); } catch { /* 数组型 content */ }
            sb.Append(role).Append(": ").Append(content ?? c?.ToJsonString() ?? "").Append("\n");
        }
        return sb.ToString();
    }

    /// <summary>取消息 role 字段；null = 非对象。</summary>
    private static string? RoleOf(JsonNode msg) => msg?.AsObject()?["role"]?.GetValue<string>();

    private static int FirstIndexOfRole(JsonArray arr, string role)
    {
        for (int i = 0; i < arr.Count; i++)
            if (RoleOf(arr[i]) == role) return i;
        return -1;
    }

    private static int LastIndexOfRole(JsonArray arr, string role)
    {
        for (int i = arr.Count - 1; i >= 0; i--)
            if (RoleOf(arr[i]) == role) return i;
        return -1;
    }

    /// <summary>取消息的文本 content（string 类型）；null = 无可裁剪内容（数组型多模态等不裁）。</summary>
    private static string? GetContent(JsonNode msg)
    {
        var c = msg?.AsObject()?["content"];
        if (c == null) return null;
        try
        {
            return c.GetValue<string>();
        }
        catch
        {
            return null; // 数组型 content：不裁
        }
    }

    private static void SetContent(JsonNode msg, string value) => msg.AsObject()["content"] = value;

    /// <summary>找可裁剪内容最长的消息下标；-1 = 无。</summary>
    private static int IndexOfLargestContent(JsonArray arr)
    {
        int best = -1, bestLen = 0;
        for (int i = 0; i < arr.Count; i++)
        {
            var c = GetContent(arr[i]);
            if (c != null && c.Length > bestLen) { bestLen = c.Length; best = i; }
        }
        return best;
    }
}
