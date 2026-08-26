using System.Net;
using System.Net.Http;

namespace LlamaLauncher;

/// <summary>
/// 智能按需调度器（监听优先、按需启动、闲置释放）：
/// - 待机态：仅用轻量 HttpListener 占用前端端口（默认 8080），零显存占用
/// - 首个请求触发唤醒：拉起 llama-server（后端端口 = 前端端口 + 1），等待就绪后代理转发，用户无感知
/// - 保活态：每次请求刷新闲置计时；连续 N 分钟（默认 15）无请求且无在途任务 → 自动 Kill 进程树释放显存
/// - 休眠后自动回到监听待机，循环待命
/// 并发请求在唤醒期间共享同一个唤醒任务排队等待，避免重复拉起多个进程。
/// </summary>
public sealed class SmartScheduler : IDisposable
{
    /// <summary>调度器状态机</summary>
    public enum Phase { Standby, Waking, Running, Sleeping }

    private readonly AppConfig _cfg;
    private readonly LlamaServerProcess _server = new();
    // 代理用 HttpClient：推理请求可能很长，禁用客户端超时
    private readonly HttpClient _hc = new() { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
    private readonly HttpListener _listener = new();
    private readonly System.Threading.Timer _tickTimer;
    private readonly object _wakeGate = new();
    private readonly object _sleepGate = new();

    private Task? _wakeTask;
    private int _inflight;                       // 在途请求计数（含排队等待唤醒的）
    private DateTime _lastTouch = DateTime.MinValue;
    private int _phase;                          // Phase 索引，统一经 Volatile.Read/Write 访问
    private int _backendPort;                    // 实际运行时后端端口（自动探测空闲）
    private readonly System.Collections.Generic.Queue<string> _recentOutput = new(); // 进程输出末几行，用于失败诊断

    /// <summary>日志行（可能来自任意线程），UI 侧负责 BeginInvoke</summary>
    public event Action<string>? Log;
    /// <summary>状态栏文本变更（可能来自任意线程），UI 侧负责 BeginInvoke</summary>
    public event Action<string>? StatusChanged;
    /// <summary>阶段切换（可能来自任意线程）</summary>
    public event Action<Phase>? PhaseChanged;

    public bool AutoMode { get; set; } = true;
    public int IdleMinutes { get; set; } = 15;

    public Phase CurrentPhase => (Phase)Volatile.Read(ref _phase);

    public SmartScheduler(AppConfig cfg)
    {
        _cfg = cfg;
        _server.OutputLine += OnServerOutput;
        _server.Exited += (_, code) => OnServerExited(code);
        _tickTimer = new System.Threading.Timer(OnTick, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>首选后端端口 = 前端端口 + 1；若被占用则向上探测空闲端口。</summary>
    private int PreferredBackendPort => Math.Min(_cfg.Port + 1, 65535);

    /// <summary>从 preferred 开始向上扫描，返回第一个可绑定的空闲端口（规避 Hyper-V/WSL2 动态端口保留）。</summary>
    private static int PickFreePort(int preferred)
    {
        var upper = Math.Min(preferred + 32, 65535);
        for (int p = preferred; p <= upper; p++)
        {
            try
            {
                var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Any, p);
                l.Start();
                l.Stop();
                return p;
            }
            catch
            {
                // 端口被占用/保留，继续上探
            }
        }
        throw new InvalidOperationException($"在 {preferred}–{upper} 范围内未找到可用后端端口。");
    }

    private void OnServerOutput(string line)
    {
        Log?.Invoke(line);
        lock (_recentOutput)
        {
            _recentOutput.Enqueue(line);
            while (_recentOutput.Count > 3) _recentOutput.Dequeue();
        }
    }

    private string RecentOutput()
    {
        lock (_recentOutput)
        {
            return string.Join(Environment.NewLine, _recentOutput);
        }
    }

    /// <summary>初始化：启动闲置计时；智能模式下开始监听前端端口。</summary>
    public void Initialize()
    {
        _tickTimer.Change(1000, 1000);
        if (AutoMode)
        {
            StartListening();
            RaiseStatus($"待机 · 监听 {_cfg.Port}，等待请求唤醒。");
        }
        else
        {
            RaiseStatus("手动模式：点击「启动 / 唤醒」运行 llama-server。");
        }
    }

    // ==================== 监听（代理入口） ====================

    private void StartListening()
    {
        if (_listener.IsListening) return;
        try
        {
            // 仅绑定本机回环，无需管理员权限
            _listener.Prefixes.Add($"http://localhost:{_cfg.Port}/");
            _listener.Prefixes.Add($"http://127.0.0.1:{_cfg.Port}/");
            _listener.Start();
            Log?.Invoke($"智能模式：已接管端口 {_cfg.Port}（llama-server 唤醒时将自动选择空闲后端端口，首选 {PreferredBackendPort}），当前显存占用为 0。");
            _ = AcceptLoopAsync();
        }
        catch (HttpListenerException ex)
        {
            Log?.Invoke($"监听端口 {_cfg.Port} 失败（可能被占用）：{ex.Message}");
        }
    }

    private void StopListening()
    {
        try
        {
            if (_listener.IsListening) _listener.Stop();
        }
        catch
        {
            // 忽略停止异常
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync();
            }
            catch
            {
                return; // 监听器已停止
            }
            _ = HandleRequestAsync(ctx);
        }
    }

    // ==================== 请求处理（排队唤醒 + 代理转发） ====================

    private async Task HandleRequestAsync(HttpListenerContext ctx)
    {
        var req = ctx.Request;

        // 本地状态探测端点：不触发唤醒、不刷新闲置计时
        var reqPath = req.Url?.AbsolutePath;
        if (string.Equals(reqPath, "/__status__", StringComparison.OrdinalIgnoreCase))
        {
            await WriteJsonAsync(ctx, 200,
                $"{{\"phase\":\"{(int)CurrentPhase}\",\"inflight\":{Volatile.Read(ref _inflight)},\"backend_port\":{_backendPort}," +
                $"\"idle_minutes\":{IdleMinutes}}}");
            return;
        }

        // 休眠释放进行中：不转发（服务正被终止），提示客户端稍后重试
        if (CurrentPhase == Phase.Sleeping)
        {
            WriteError(ctx, 502, "LLM 服务正在休眠释放，请稍后重试。");
            return;
        }

        Interlocked.Increment(ref _inflight);
        try
        {
            // 首请求排队等待唤醒完成（共享同一唤醒任务，防多进程冲突）
            await EnsureRunningAsync();
            // 只有真实推理请求才刷新闲置计时；探测类请求（GET /v1/models、健康检查等）
            // 不算使用——否则 Agent 周期性轮询会把倒计时无限续命，导致永不休眠
            bool isInference = IsInferenceRequest(req);
            if (isInference) Touch();
            await ForwardAsync(ctx);       // 代理转发到后端 llama-server（流式直通）
            if (isInference) Touch();      // 请求完成：再次刷新倒计时
        }
        catch (Exception ex)
        {
            Log?.Invoke($"请求处理失败：{ex.Message}");
            WriteError(ctx, 503, ex.Message);
        }
        finally
        {
            Interlocked.Decrement(ref _inflight);
        }
    }

    /// <summary>确保后端服务运行；未运行时排队等待唤醒任务。</summary>
    private async Task EnsureRunningAsync()
    {
        if (_server.IsRunning) return;
        Task t;
        lock (_wakeGate)
        {
            t = _wakeTask ??= WakeUpAsync();
        }
        await t;
    }

    /// <summary>
    /// 唤醒流程：校验 exe/模型 → 按黄金底参启动 llama-server（后端端口）→ 轮询就绪。
    /// 失败时清理刚拉起的进程，回到待机，异常抛给调用方。
    /// </summary>
    private async Task WakeUpAsync()
    {
        SetPhase(Phase.Waking);
        RaiseStatus("唤醒中…（正在加载模型）");
        try
        {
            var exe = LlamaFinder.Find(_cfg.ExePath)
                ?? throw new InvalidOperationException("未找到 llama-server.exe，请先在界面指定路径。");
            if (string.IsNullOrWhiteSpace(_cfg.ModelPath) || !File.Exists(_cfg.ModelPath))
                throw new InvalidOperationException($"模型文件不存在：{_cfg.ModelPath}");

            // 智能模式下自动探测空闲后端端口，规避 Hyper-V/WSL2 动态端口保留导致的绑定失败
            int srvPort = AutoMode ? PickFreePort(PreferredBackendPort) : _cfg.Port;
            _backendPort = srvPort;
            var args = LlamaFinder.BuildArgs(_cfg, srvPort);
            Log?.Invoke($"唤醒 llama-server：{Path.GetFileName(exe)} {args}");

            _server.Start(exe, args, Path.GetDirectoryName(Path.GetFullPath(exe))!);

            // 13900F 纯大核绑定：按配置掩码绑定 P 核（留空 = 禁用）
            string? affinityDesc = CpuAffinity.Apply(_server.Current, _cfg.PCoreMask);
            Log?.Invoke(affinityDesc != null ? $"P核绑定生效：{affinityDesc}" : "P核绑定已禁用（掩码为空或无效）。");

            await WaitReadyAsync(srvPort);

            Touch();
            SetPhase(Phase.Running);
            Log?.Invoke("llama-server 就绪，进入保活状态。");
            _cfg.Save(); // 唤醒成功：持久化当前参数
        }
        catch (Exception)
        {
            try { _server.Stop(); } catch { } // 清理失败时拉起的进程，防残留
            SetPhase(Phase.Standby);
            RaiseStatus($"唤醒失败，回到待机。");
            throw;
        }
        finally
        {
            lock (_wakeGate) { _wakeTask = null; }
        }
    }

    /// <summary>轮询后端 /v1/models 直至就绪（最长 5 分钟），期间进程退出立即报错。</summary>
    private async Task WaitReadyAsync(int srvPort)
    {
        var url = $"http://localhost:{srvPort}/v1/models";
        var deadline = DateTime.Now + TimeSpan.FromMinutes(5);
        while (DateTime.Now < deadline)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                using var r = await _hc.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                if (r.IsSuccessStatusCode) return;
            }
            catch
            {
                // 连接拒绝 / 超时：服务尚未就绪，继续轮询
            }
            if (!_server.IsRunning)
                throw new InvalidOperationException(
                    "llama-server 进程已退出，唤醒失败。\n最近输出：\n" + RecentOutput());
            await Task.Delay(2000);
        }
        throw new TimeoutException("等待 llama-server 就绪超时（5 分钟）。");
    }

    /// <summary>把请求原样转发到后端；ResponseHeadersRead + CopyToAsync 保证 SSE/流式响应直通。</summary>
    private async Task ForwardAsync(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var uri = new Uri($"http://localhost:{_backendPort}{req.RawUrl}");

        using var msg = new HttpRequestMessage(new HttpMethod(req.HttpMethod), uri)
        {
            Content = new StreamContent(req.InputStream),
        };
        foreach (string key in req.Headers)
        {
            // Host / 长度 / 编码类头由 HttpClient 自行处理，避免冲突
            if (key.Equals("Host", StringComparison.OrdinalIgnoreCase)) continue;
            if (key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
            if (key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)) continue;
            if (key.Equals("Connection", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                msg.Headers.TryAddWithoutValidation(key, req.Headers[key]);
            }
            catch
            {
                // 个别特殊头无法原样复制，忽略
            }
        }

        var resp = await _hc.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead);
        var outResp = ctx.Response;
        outResp.StatusCode = (int)resp.StatusCode;
        var ct = resp.Content.Headers.ContentType?.ToString();
        outResp.ContentType = string.IsNullOrEmpty(ct) ? "application/octet-stream" : ct!;
        await resp.Content.CopyToAsync(outResp.OutputStream);
        outResp.Close();
    }

    /// <summary>判断是否为真实推理请求（刷新闲置计时）：POST + completions/embeddings 路径。</summary>
    private static bool IsInferenceRequest(HttpListenerRequest req)
    {
        if (!string.Equals(req.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase)) return false;
        var p = req.Url?.AbsolutePath ?? "";
        return p.Contains("completion", StringComparison.OrdinalIgnoreCase)
            || p.Contains("embedding", StringComparison.OrdinalIgnoreCase);
    }

    // ==================== 闲置休眠（15 分钟无请求自动释放） ====================

    /// <summary>刷新闲置倒计时基准点。</summary>
    private void Touch() => _lastTouch = DateTime.Now;

    private int _tickCount;

    private void OnTick(object? _)
    {
        if (CurrentPhase != Phase.Running) return;
        int inflight = Volatile.Read(ref _inflight);
        var remaining = _lastTouch.Add(TimeSpan.FromMinutes(IdleMinutes)) - DateTime.Now;
        if (remaining <= TimeSpan.Zero && inflight == 0)
            SleepNow();
        else if (inflight > 0)
            // 有在途任务时不触发休眠，明确提示原因（长驻 SSE 流式连接会一直压制休眠）
            RaiseStatus($"运行中 · {inflight} 个在途任务，休眠暂停");
        else
            RaiseStatus($"运行中 · {(int)remaining.TotalMinutes:D2}:{remaining.Seconds:D2} 无请求后自动休眠");

        // P 核亲和性自愈：每 5 秒检查一次，被系统重置时自动重绑
        if (++_tickCount % 5 == 0 && CpuAffinity.Heal(_server.Current, _cfg.PCoreMask))
            Log?.Invoke("检测到 CPU 亲和性被重置，已重新绑定 P 核。");
    }

    /// <summary>安全停机：仅当无在途任务时执行；Kill 整个进程树，杜绝残留。</summary>
    private void SleepNow()
    {
        lock (_sleepGate)
        {
            if (CurrentPhase != Phase.Running) return;
            SetPhase(Phase.Sleeping);
        }
        Log?.Invoke($"{IdleMinutes} 分钟无请求，自动休眠，正在释放显存…");
        RaiseStatus("闲置超时，正在释放显存…");
        _server.Stop(); // Exited 事件将把状态拉回 Standby
    }

    /// <summary>进程退出回调：休眠/运行态退出 → 回到监听待机；唤醒态由唤醒任务自行处理。</summary>
    private void OnServerExited(int code)
    {
        var p = CurrentPhase;
        if (p == Phase.Sleeping || p == Phase.Running)
        {
            SetPhase(Phase.Standby);
            Log?.Invoke($"llama-server 已退出（退出码 {code}），显存已释放，回到监听待机。");
            RaiseStatus(AutoMode ? "已休眠，继续监听待机。" : "已停止。");
        }
    }

    // ==================== 对外控制接口 ====================

    /// <summary>启动 / 唤醒按钮：立即拉起后端服务（含就绪等待）。</summary>
    public Task LaunchNowAsync() => EnsureRunningAsync();

    /// <summary>停止按钮 / 关闭前：终止进程树。</summary>
    public void StopNow()
    {
        Log?.Invoke("正在停止 llama-server…");
        SetPhase(Phase.Standby); // 先置位，Exited 回调不再重复报告
        RaiseStatus(AutoMode ? "已停止，监听待机中。" : "已停止。");
        _server.Stop();
    }

    /// <summary>智能模式开关（可实时切换）。</summary>
    public void SetAutoMode(bool on)
    {
        if (on == AutoMode) return;
        AutoMode = on;
        if (on)
        {
            if (_server.IsRunning)
            {
                Log?.Invoke("切换到智能模式：先停止当前服务。");
                StopNow();
            }
            StartListening();
            RaiseStatus($"待机 · 监听 {_cfg.Port}，等待请求唤醒。");
        }
        else
        {
            StopListening();
            if (_server.IsRunning)
            {
                Log?.Invoke("切换到手动模式：停止当前服务。");
                StopNow();
            }
            RaiseStatus("手动模式：点击「启动 / 唤醒」运行 llama-server。");
        }
    }

    public void Dispose()
    {
        StopListening();
        SetPhase(Phase.Standby);
        try { _server.Stop(); } catch { }
        _tickTimer.Dispose();
        _hc.Dispose();
        _server.Dispose();
    }

    // ==================== 状态与工具 ====================

    private void SetPhase(Phase p)
    {
        if (CurrentPhase == p) return;
        Volatile.Write(ref _phase, (int)p);
        PhaseChanged?.Invoke(p);
    }

    private void RaiseStatus(string text) => StatusChanged?.Invoke(text);

    private static async Task WriteJsonAsync(HttpListenerContext ctx, int code, string json)
    {
        var resp = ctx.Response;
        resp.StatusCode = code;
        resp.ContentType = "application/json";
        resp.ContentEncoding = System.Text.Encoding.UTF8;
        var buf = System.Text.Encoding.UTF8.GetBytes(json);
        resp.ContentLength64 = buf.Length;
        await resp.OutputStream.WriteAsync(buf);
        resp.Close();
    }

    private static void WriteError(HttpListenerContext ctx, int code, string msg)
    {
        try
        {
            var safe = msg.Replace("\\", "\\\\").Replace("\"", "\\\"");
            var resp = ctx.Response;
            resp.StatusCode = code;
            resp.ContentType = "application/json";
            resp.ContentEncoding = System.Text.Encoding.UTF8;
            var body = $"{{\"error\":\"{safe}\"}}";
            var buf = System.Text.Encoding.UTF8.GetBytes(body);
            resp.ContentLength64 = buf.Length;
            resp.OutputStream.Write(buf);
            resp.Close();
        }
        catch
        {
            // 客户端可能已断开，忽略
        }
    }
}
