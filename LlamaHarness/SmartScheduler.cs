using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace LlamaHarness;

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
    // 代理用 HttpClient：推理请求可能很长，禁用客户端超时。
    // Connection: close —— 不复用池化 keep-alive 连接：llama-server 会关闭空闲连接、
    // 休眠/唤醒后旧端口残留死连接，复用都会报 "An error occurred while sending the request."
    private readonly HttpClient _hc = new()
    {
        Timeout = System.Threading.Timeout.InfiniteTimeSpan,
        DefaultRequestHeaders = { { "Connection", "close" } },
    };
    private readonly HttpListener _listener = new();
    private readonly System.Threading.Timer _tickTimer;
    private readonly object _wakeGate = new();
    private readonly object _sleepGate = new();

    private Task? _wakeTask;
    private int _inflight;                       // 在途请求计数（含排队等待唤醒的）
    private long _lastTouchTicks = DateTime.Now.Ticks; // 闲置计时基准（Interlocked 保护，跨线程读写）
    private int _phase;                          // Phase 索引，统一经 Volatile.Read/Write 访问
    private volatile int _backendPort;           // 实际运行时后端端口（自动探测空闲）
    private int _tickCount;                      // 秒级 tick 计数（定时器周期 1s），用于周期性自愈检查
    private readonly System.Collections.Generic.Queue<string> _recentOutput = new(); // 进程输出末几行，用于失败诊断

    /// <summary>P 核亲和性自愈检查间隔（tick 数，定时器周期 1s）：每 5 秒核对一次绑定是否被系统重置。</summary>
    private const int AffinityHealEveryTicks = 5;

    // —— 审计 O-11：魔法数字提常量（原散落各处的裸数值） ——
    /// <summary>休眠静默观察期时长（秒）：期间新请求/在途任务取消休眠。</summary>
    private const int SleepGraceSeconds = 10;
    /// <summary>request_dump.log 请求体截断长度（字符），防大请求撑爆磁盘。</summary>
    private const int DumpBodyMaxLength = 2000;
    /// <summary>休眠后显存告警阈值（MB）：高于此值疑似子进程残留。</summary>
    private const int VramAlertThresholdMb = 1024;
    /// <summary>崩溃恢复内存余量阈值（GB）：空闲 RAM 低于此值时重放预算收紧。</summary>
    private const double TightMemoryFreeGb = 4.0;
    /// <summary>崩溃恢复预算收紧系数（严格预算 = 基础预算 × 此系数）。</summary>
    private const double TightBudgetFactor = 0.75;
    /// <summary>bad_alloc 日志佐证窗口（秒）：该窗口内出现过 bad_alloc 关键字才认可 5xx 响应体判定。</summary>
    private static readonly TimeSpan BadAllocEvidenceWindow = TimeSpan.FromSeconds(60);

    /// <summary>日志行（可能来自任意线程），UI 侧负责 BeginInvoke</summary>
    public event Action<string>? Log;
    /// <summary>状态栏文本变更（可能来自任意线程），UI 侧负责 BeginInvoke</summary>
    public event Action<string>? StatusChanged;
    /// <summary>阶段切换（可能来自任意线程）</summary>
    public event Action<Phase>? PhaseChanged;
    /// <summary>C-007：进入 Waking 阶段时触发，UI 据此重置统计解析器（职责下沉到调度器内部，不再依赖 UI 自行监听 PhaseChanged）。</summary>
    public event Action? StatsReset;
    /// <summary>思考模式状态变更（可能来自任意线程），UI 侧负责 BeginInvoke。参数为当前档位。</summary>
    public event Action<ThinkingLevel>? ThinkingModeChanged;
    /// <summary>槽位绑定变更（新绑定/驱逐），UI 侧刷新槽位表格。</summary>
    public event Action? SlotBindingChanged;
    /// <summary>槽位相关日志（绑定/驱逐/KV Cache 保存恢复，可能来自任意线程）：UI 显示于槽位页 + 持久化 slot.log。</summary>
    public event Action<string>? SlotLog;

    /// <summary>槽位事件双写：主日志（UI 显示 + harness.log）+ slot.log / 槽位页（审计 O-10：收敛此前 10+ 处成对 Invoke 样板）。</summary>
    private void EmitSlot(string msg)
    {
        Log?.Invoke(msg);
        SlotLog?.Invoke(msg);
    }

    // C-102 运行统计埋点
    private int _wakeCount, _sleepCount, _inflightPeak;

    /// <summary>思考模式三档状态机（lock 保护，多 agent 并发安全）。默认 Off = 极速模式（65+ t/s）。</summary>
    private ThinkingLevel _thinkingMode = ThinkingLevel.Off;
    private readonly object _thinkingGate = new();

    /// <summary>当前思考模式档位（线程安全读取）。</summary>
    public ThinkingLevel ThinkingMode { get { lock (_thinkingGate) return _thinkingMode; } }

    /// <summary>程序化设置思考模式档位（UI 按钮调用）：线程安全，触发 ThinkingModeChanged + 日志。
    /// 与聊天指令切换同属运行态开关——不跨会话携带，唤醒时按启动参数重置基线。</summary>
    public void SetThinkingMode(ThinkingLevel level)
    {
        lock (_thinkingGate) { _thinkingMode = level; }
        Log?.Invoke($"思考模式已切换为「{LabelOf(level)}」（{(EffortOf(level) is var e && e != null ? $"reasoning_effort={e}, " : "")}enable_thinking={(level == ThinkingLevel.Off ? "false" : "true")}）。");
        ThinkingModeChanged?.Invoke(level);
    }

    /// <summary>多槽亲和绑定管理器（--parallel &gt; 1 时创建；null = 单槽不路由）。</summary>
    private volatile SlotAffinity? _affinity;

    /// <summary>KV Cache 管理器（--parallel &gt; 1 且 KvCachePath 非空时创建；null = 禁用）。</summary>
    private volatile KvCacheManager? _kvCache;

    // ==================== KV 全场景复用状态（§4.1/§4.5/§8，多 agent 并发请求共享）====================

    /// <summary>KV 复用状态统一门控（_truncPending / _toolLockedKeys / _prefixHashes 共用）。</summary>
    private readonly object _kvStateGate = new();
    /// <summary>截断待续接标记（§4.1）：已 save 断点快照且续接中的 key。续接成功 → 清理过期快照；失败 → 保留供 restore。</summary>
    private readonly HashSet<string> _truncPending = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Tool 链锁定集合（§4.5）：本层执行过 SetPreemptive(true) 的 key。只解锁集合内的键，不碰用户手动/自动强占。</summary>
    private readonly HashSet<string> _toolLockedKeys = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>前缀哈希表（§8 可观测）：key → SHA256(最新一轮之前的全部 messages JSON)。比对判定原生 KV 前缀复用 HIT/MISS。</summary>
    private readonly Dictionary<string, string> _prefixHashes = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>本进程运行以来已服务过的亲和 key（唤醒时清空）：「进程重启后该 key 首次使用 → restore KV 自愈」判定依据，
    /// 防止进程存活期间误用磁盘旧快照回退内存中更新的槽位状态。</summary>
    private readonly HashSet<string> _servedKeysThisRun = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>休眠静默观察期进行中标志（_sleepGate 保护）：防闲置定时器重复触发休眠流程。</summary>
    private bool _sleepPreparing;

    /// <summary>从启动附加参数判定初始思考档位：
    /// --reasoning on → XHigh（深度推理）；--reasoning off 或无该参数 → Off（默认不思考）。
    /// 注意：仅显式 on 才开启思考，避免默认注入深度思考干扰严格 JSON 类请求（如意图分类器）。</summary>
    public static ThinkingLevel DetermineInitialThinkingMode(string extraArgs)
    {
        if (string.IsNullOrWhiteSpace(extraArgs)) return ThinkingLevel.Off; // 无参数 = 默认不思考
        var m = System.Text.RegularExpressions.Regex.Match(
            extraArgs,
            @"--reasoning\s+(on|off)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return (m.Success && m.Groups[1].Value.Equals("on", StringComparison.OrdinalIgnoreCase))
            ? ThinkingLevel.XHigh
            : ThinkingLevel.Off;
    }

    /// <summary>思考模式档位：Off=极速（不注入）/ Low / Medium / XHigh。Low~XHigh 均携带 enable_thinking=true。</summary>
    public enum ThinkingLevel { Off, Low, Medium, XHigh }

    /// <summary>档位 → reasoning_effort 值；Off 返回 null（不注入）。</summary>
    public static string? EffortOf(ThinkingLevel lvl) => lvl switch
    {
        ThinkingLevel.Low => "low",
        ThinkingLevel.Medium => "medium",
        ThinkingLevel.XHigh => "xhigh",
        _ => null,
    };

    /// <summary>档位显示名。</summary>
    public static string LabelOf(ThinkingLevel lvl) => lvl switch
    {
        ThinkingLevel.Off => "极速",
        ThinkingLevel.Low => "轻度推理",
        ThinkingLevel.Medium => "中度推理",
        _ => "深度推理",
    };

    public bool AutoMode { get; set; } = true;
    public int IdleMinutes { get; set; } = 15;

    public Phase CurrentPhase => (Phase)Volatile.Read(ref _phase);

    /// <summary>获取槽位绑定快照（UI 表格刷新用，含应用名/强占/KV缓存配置）。null = 未启用多槽。</summary>
    public List<(string Key, string App, int Slot, DateTime LastActive, bool Preemptive, bool KvCache)>? GetSlotBindings()
    {
        var aff = _affinity;
        return aff?.Snapshot();
    }

    /// <summary>设置指定绑定的强占模式（UI 槽位管理页调用）。</summary>
    public void SetSlotPreemptive(string key, bool value) => _affinity?.SetPreemptive(key, value);

    /// <summary>设置指定绑定的 KV Cache 开关（UI 槽位管理页调用）。</summary>
    public void SetSlotKvCache(string key, bool value) => _affinity?.SetKvCache(key, value);

    /// <summary>获取 KV Cache 管理器（UI 清空缓存用）。null = 未启用。</summary>
    public KvCacheManager? GetKvCache() => _kvCache;

    public SmartScheduler(AppConfig cfg)
    {
        _cfg = cfg;
        _server.OutputLine += OnServerOutput;
        _server.Exited += (_, code) => OnServerExited(code);
        _tickTimer = new System.Threading.Timer(OnTick, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>首选后端端口 = 前端端口 + 1；若被占用则向上探测空闲端口。</summary>
    private int PreferredBackendPort => Math.Min(_cfg.Port + 1, 65535);

    /// <summary>从 preferred 开始向上扫描，返回第一个可绑定的空闲端口（规避 Hyper-V/WSL2 动态端口保留）。
    /// 注意：探测与 llama-server 实际绑定之间存在极小的 TOCTOU 窗口；若该窗口内端口被抢占，
    /// llama-server 绑定失败会自行退出，WaitReadyAsync 检测到进程退出并上报失败，下次唤醒重新探测——本机单用户场景可接受。</summary>
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
        // bad_alloc 检测：llama.cpp 任务级 OOM（"got exception: bad allocation"）→ 记录观测事件供流中断佐证
        CrashRecovery.OnBackendLine(line);
        if (line.Contains("bad allocation", StringComparison.OrdinalIgnoreCase))
            Log?.Invoke($"⚠ 检测到后端 bad_alloc（任务级内存耗尽），已记录崩溃事件。");
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
        int failures = 0;
        while (_listener.IsListening)
        {
            HttpListenerContext? ctx = null;
            bool got = false;
            try
            {
                ctx = await _listener.GetContextAsync();
                got = true;
                failures = 0;
            }
            catch (Exception ex)
            {
                // C-008：运行期监听异常（端口抢占/睡眠唤醒/权限变更）——记录 + 有限次数重试
                if (!_listener.IsListening) return; // 正常停止，静默退出
                Log?.Invoke($"错误：监听异常（{ex.Message}），尝试重新监听…");
                if (++failures >= 3)
                {
                    RaiseStatus("监听失败：端口不可用，请检查端口后重启智能模式。");
                    return;
                }
                await Task.Delay(2000);
                try
                {
                    _listener.Stop();
                    _listener.Start();
                    Log?.Invoke("监听已重新建立。");
                }
                catch (Exception ex2)
                {
                    Log?.Invoke($"错误：重新监听失败：{ex2.Message}");
                    RaiseStatus("监听失败：端口不可用，请检查端口后重启智能模式。");
                    return;
                }
            }
            if (got && ctx != null) _ = HandleRequestAsync(ctx); // 仅成功取到请求时处理；重试后回到循环顶部
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
            // C-103：phase 输出枚举名、idle_minutes 为当前已闲置分钟数（动态值）+ 配置阈值、
            // recent_logs 取 LogFile 环形缓冲（含 harness 侧日志），供外部 Agent 远程诊断
            var aff = _affinity;
            var idleMinutes = (DateTime.Now - new DateTime(Interlocked.Read(ref _lastTouchTicks))).TotalMinutes;
            var payload = new
            {
                phase = CurrentPhase.ToString(),
                inflight = Volatile.Read(ref _inflight),
                backend_port = _backendPort,
                idle_minutes = Math.Round(idleMinutes, 1),
                idle_threshold_minutes = IdleMinutes,
                slots = aff == null ? null : new
                {
                    count = aff.SlotCount,
                    bindings = aff.Snapshot().ToDictionary(
                        kv => kv.Key,
                        kv => new { slot = kv.Slot, last_active = kv.LastActive }),
                },
                recent_logs = LogFile.SnapshotRecent(),
            };
            await WriteJsonAsync(ctx, 200, System.Text.Json.JsonSerializer.Serialize(payload));
            return;
        }

        // 休眠释放进行中：不转发（服务正被终止），提示客户端稍后重试
        if (CurrentPhase == Phase.Sleeping)
        {
            WriteError(ctx, 502, "LLM 服务正在休眠释放，请稍后重试。");
            return;
        }

        bool isInference = IsInferenceRequest(req);

        // 探测类请求（GET /v1/models、健康检查等）无唤醒权：
        // 服务运行时照常代理；待机/休眠时直接拒绝，防止 Agent 周期性轮探
        // 把刚休眠的服务反复唤醒（唤醒→15分钟倒计时→再休眠→再唤醒循环）
        if (!isInference && !_server.IsRunning)
        {
            WriteError(ctx, 503, "LLM 服务处于待机/休眠状态，仅推理请求可触发唤醒。");
            return;
        }

        int cur = Interlocked.Increment(ref _inflight);
        if (cur > Volatile.Read(ref _inflightPeak)) Volatile.Write(ref _inflightPeak, cur); // C-102 峰值记录
        try
        {
            // 首请求排队等待唤醒完成（共享同一唤醒任务，防多进程冲突）
            await EnsureRunningAsync();
            // 只有真实推理请求才刷新闲置计时；探测类请求不算使用
            if (isInference) Touch();
            await ForwardAsync(ctx);       // 代理转发到后端 llama-server（流式直通）
            if (isInference) Touch();      // 请求完成：再次刷新倒计时
        }
        catch (Exception ex)
        {
            // 带上内层异常细节，便于定位（如连接重置 vs 超时）
            var detail = ex.InnerException != null ? $"（内层：{ex.InnerException.Message}）" : "";
            Log?.Invoke($"请求处理失败：{ex.Message}{detail}");
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
        _nonStreamWarned = 0; // 新会话：非流式告警重新计数
        StatsReset?.Invoke();   // C-007：进入 Waking 即重置统计（llama-server task ID 从 0 重计），不再依赖 UI 调用
        SetPhase(Phase.Waking);
        RaiseStatus("唤醒中…（正在加载模型）");
        var wakeStart = DateTime.Now; // C-102：唤醒耗时计时
        try
        {
            var exe = LlamaFinder.Find(_cfg.ExePath)
                ?? throw new InvalidOperationException("未找到 llama-server.exe，请先在界面指定路径。");
            if (string.IsNullOrWhiteSpace(_cfg.ModelPath) || !File.Exists(_cfg.ModelPath))
                throw new InvalidOperationException($"模型文件不存在：{_cfg.ModelPath}");

            // 智能模式下自动探测空闲后端端口，规避 Hyper-V/WSL2 动态端口保留导致的绑定失败
            int srvPort = AutoMode ? PickFreePort(PreferredBackendPort) : _cfg.Port;
            _backendPort = srvPort;

            // P 核掩码生效时线程数不得超过掩码绑定的核数，否则超订降速
            int threads = _cfg.Threads;
            var pcoreMask = CpuAffinity.ParseMask(_cfg.PCoreMask);
            if (pcoreMask != null)
            {
                int coreCount = System.Numerics.BitOperations.PopCount((ulong)pcoreMask.Value); // 掩码恒为正，转 ulong 安全
                if (threads > coreCount)
                {
                    Log?.Invoke($"注意：线程数 {threads} 超出 P 核掩码的 {coreCount} 核，本次启动钳制为 {coreCount}（超订会降速）。建议调整线程数参数。");
                    threads = coreCount;
                }
            }

            // --host 使后端监听非本机地址：绕过代理闲置休眠逻辑并把模型暴露到局域网
            if (_cfg.ExtraArgs.Contains("--host", StringComparison.OrdinalIgnoreCase))
                Log?.Invoke("警告：附加参数含 --host，后端可能监听非本机地址，将暴露到局域网并绕过闲置休眠。建议移除。");

            var args = LlamaFinder.BuildArgs(_cfg, srvPort, threads);
            Log?.Invoke($"唤醒 llama-server：{Path.GetFileName(exe)} {args}");

            _server.Start(exe, args, Path.GetDirectoryName(Path.GetFullPath(exe))!);

            // 13900F 纯大核绑定：按配置掩码绑定 P 核（留空 = 禁用）
            string? affinityDesc = CpuAffinity.Apply(_server.Current, _cfg.PCoreMask);
            Log?.Invoke(affinityDesc != null ? $"P核绑定生效：{affinityDesc}" : "P核绑定已禁用（掩码为空或无效）。");

            // 思考模式基线：新服务进程按本次启动参数重置（运行态指令切换不跨会话携带）
            var baseLevel = DetermineInitialThinkingMode(_cfg.ExtraArgs);
            lock (_thinkingGate) { _thinkingMode = baseLevel; }
            ThinkingModeChanged?.Invoke(baseLevel);
            Log?.Invoke($"思考模式基线：「{LabelOf(baseLevel)}」（{(EffortOf(baseLevel) is var be && be != null ? $"reasoning_effort={be}, " : "")}enable_thinking={(baseLevel == ThinkingLevel.Off ? "false" : "true")}）。");

            // 槽位亲和：始终启用（单槽/多槽均激活），指纹绑定 + n_slots 路由
            _affinity = new SlotAffinity(_cfg.Parallel);
            Log?.Invoke($"槽位亲和已启用：{_cfg.Parallel} 槽，指纹绑定 + n_slots 路由（绑定表 slot_bindings.json，LRU 驱逐）。");

            // KV Cache 持久化：KvCachePath 非空时启用（驱逐 save / 重绑定 restore / 休眠前 save / 唤醒后 restore）
            _kvCache = !string.IsNullOrWhiteSpace(_cfg.KvCachePath)
                ? new KvCacheManager(_hc, _cfg.KvCachePath, _cfg.Parallel, srvPort)
                : null;
            if (_kvCache != null)
                Log?.Invoke($"KV Cache 持久化已启用：路径 {_cfg.KvCachePath}（驱逐自动 save，重绑定自动 restore，休眠前自动 save，唤醒后自动 restore）。");

            // 新进程槽位 KV 全空：清空「本轮已服务」标记 → 唤醒后各 key 首次请求触发 restore 自愈（跳过全量 prefill）
            lock (_kvStateGate) _servedKeysThisRun.Clear();

            await WaitReadyAsync(srvPort);

            Touch();
            SetPhase(Phase.Running);
            // C-102：唤醒统计埋点（累计次数 + 本次耗时）
            Interlocked.Increment(ref _wakeCount);
            var elapsed = (DateTime.Now - wakeStart).TotalSeconds;
            Log?.Invoke($"llama-server 就绪，进入保活状态。（唤醒 #{Volatile.Read(ref _wakeCount)}，本次耗时 {elapsed:F1}s）");
            // 唤醒成功：持久化当前参数
            if (!_cfg.Save(out string? saveErr))
                Log?.Invoke($"警告：配置持久化失败（{saveErr}），下次启动不会恢复本次参数。");
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

    /// <summary>轮询后端 /v1/models 直至就绪（最长 5 分钟），期间进程退出立即报错。
    /// C-003：不仅校验 HTTP 200，还校验响应内容含 "object":"list" 模型列表特征——
    /// 防 TOCTOU 窗口内其他程序抢占后端端口时被误判为 llama-server 就绪。
    /// 每 15 秒输出一次进度（大模型加载可达数分钟），避免界面无反馈看似卡死。</summary>
    private async Task WaitReadyAsync(int srvPort)
    {
        var url = $"http://localhost:{srvPort}/v1/models";
        var deadline = DateTime.Now + TimeSpan.FromMinutes(5);
        var start = DateTime.Now;
        int nextProgressAtSec = 10; // 下次进度日志的累计秒数阈值
        while (DateTime.Now < deadline)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                using var r = await _hc.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                if (r.IsSuccessStatusCode)
                {
                    var body = await r.Content.ReadAsStringAsync(cts.Token);
                    // 内容特征校验：llama-server /v1/models 返回 {"object":"list",...}
                    if (body.Contains("\"object\":\"list\"")) return;
                }
            }
            catch
            {
                // 连接拒绝 / 超时：服务尚未就绪，继续轮询
            }
            if (!_server.IsRunning)
                throw new InvalidOperationException(
                    "llama-server 进程已退出，唤醒失败。\n最近输出：\n" + RecentOutput());

            // 进度反馈：大模型（数十 GB）加载耗时可达数分钟，期间静默等待易被误判为卡死
            int elapsedSec = (int)(DateTime.Now - start).TotalSeconds;
            if (elapsedSec >= nextProgressAtSec)
            {
                nextProgressAtSec = elapsedSec + 15;
                var lastLine = RecentOutput().Split('\n').LastOrDefault()?.Trim();
                Log?.Invoke($"等待 llama-server 就绪… {elapsedSec}s（正在加载模型/显存分配。最新输出：{(string.IsNullOrEmpty(lastLine) ? "无" : lastLine)}）");
            }
            await Task.Delay(2000);
        }
        throw new TimeoutException("等待 llama-server 就绪超时（5 分钟）。");
    }

    /// <summary>把请求原样转发到后端；ResponseHeadersRead + CopyToAsync 保证 SSE/流式响应直通。
    /// 审计 O-8：按管道阶段拆分为 读体 → 网关预处理 → 转发管道 → 完成清理 四段，本方法仅做编排。</summary>
    private async Task ForwardAsync(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var uri = new Uri($"http://localhost:{_backendPort}{req.RawUrl}");
        string path = req.Url?.AbsolutePath ?? "";

        // ① 读取完整请求体（非流式检测 / 强制流式改写需要）；GET 无请求体
        byte[]? bodyBytes = await ReadRequestBodyAsync(req);

        // 请求体 dump（应用识别分析用）：每个 POST 请求的原始 body + headers 落盘；O-18：默认关闭，配置开启才生效（防 prompt 隐私落盘与无谓 IO）
        if (bodyBytes != null && bodyBytes.Length > 0 && _cfg.RequestDumpEnabled)
            DumpRequest(ctx, bodyBytes);

        // ② 网关预处理：思考模式拦截 / 槽位亲和与 KV restore / TokenGuard / 强制流式 / 前缀哈希
        string? finalBody = null;   // 最终请求体（网关改写后），供输出续接构造下一轮
        bool effStreaming = false;  // 有效流式（含 ForceStream 改写）
        int? routedSlot = null;     // 本次请求亲和路由的槽位号（崩溃恢复快照接续用）
        string? routedKey = null;   // 本次请求亲和路由的绑定 key（KV 快照文件名）
        if (bodyBytes != null && bodyBytes.Length > 0)
        {
            var prepared = await PrepareGatewayAsync(ctx, req, path, bodyBytes);
            if (prepared == null) return; // TokenGuard 拒绝：响应已写出
            (bodyBytes, finalBody, effStreaming, routedSlot, routedKey) = prepared.Value;
        }

        // ③ 转发后端 + 响应管道 + 完成清理
        await SendAndPipeAsync(ctx, uri, path, req, bodyBytes, finalBody, effStreaming, routedSlot, routedKey);
    }

    /// <summary>读取请求体字节（仅 POST；GET 返回 null）。</summary>
    private static async Task<byte[]?> ReadRequestBodyAsync(HttpListenerRequest req)
    {
        if (!string.Equals(req.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
            return null;
        using var ms = new MemoryStream();
        await req.InputStream.CopyToAsync(ms);
        return ms.ToArray();
    }

    /// <summary>网关预处理管道（仅推理请求）：
    /// 思考模式拦截 → 槽位亲和路由 + Tool 链锁定 + KV 驱逐 save / restore 自愈 → TokenGuard 裁剪 → 强制流式改写 → 前缀哈希可观测。
    /// 返回 (改写后 bodyBytes, finalBody, effStreaming, routedSlot, routedKey)；返回 null = TokenGuard 拒绝（已向客户端写 400）。</summary>
    private async Task<(byte[] BodyBytes, string FinalBody, bool EffStreaming, int? RoutedSlot, string? RoutedKey)?> PrepareGatewayAsync(
        HttpListenerContext ctx, HttpListenerRequest req, string path, byte[] bodyBytes)
    {
        string p = req.Url?.AbsolutePath ?? "";
        bool isCompletions = p.Contains("completion", StringComparison.OrdinalIgnoreCase)
                             || p.Contains("embedding", StringComparison.OrdinalIgnoreCase);
        if (!isCompletions)
            return null; // 非推理请求：不做网关处理（finalBody=null 走纯透传管道）

        string body = System.Text.Encoding.UTF8.GetString(bodyBytes);

        // E-1/E-3：入口一次性解析 → 后续所有阶段复用同一棵 DOM，管道末端只序列化一次。
        // 解析失败（非法 JSON）→ root=null → 跳过全部 DOM 改写、原样透传（等价于旧实现各方法 try-catch 透传）。
        JsonObject? root = null;
        try { root = JsonNode.Parse(body)?.AsObject(); } catch { /* 非法 JSON */ }

        int? routedSlot = null;
        string? routedKey = null;

        // 思考模式拦截（仅 chat/completions）：识别指令 / 注入 reasoning_effort + enable_thinking / 校验修正非法档位
        if (IsChatCompletions(p) && root != null)
        {
            ThinkingLevel lvl, prev;
            bool changed;
            string? effortFix = null;
            lock (_thinkingGate)
            {
                prev = _thinkingMode;
                lvl = _thinkingMode;
                InjectThinkingMode(root, ref lvl, out effortFix); // DOM 版：原地改树，不再 parse/serialize
                changed = lvl != prev;
                _thinkingMode = lvl;
            }
            if (changed)
            {
                Log?.Invoke($"思考模式已切换为「{LabelOf(lvl)}」（{(EffortOf(lvl) is var e && e != null ? $"reasoning_effort={e}, " : "")}enable_thinking={(lvl == ThinkingLevel.Off ? "false" : "true")}）。");
                ThinkingModeChanged?.Invoke(lvl);
            }
            if (effortFix != null)
                Log?.Invoke($"思考参数清洗：{effortFix}。");
        }

        // 槽位亲和路由（单槽/多槽均启用）：指纹绑定 + 注入 n_slots 固定槽位；槽忙时 llama.cpp 原生排队，不跨槽漂移
        var aff = _affinity;
        if (aff != null && p.Contains("completion", StringComparison.OrdinalIgnoreCase))
        {
            (routedSlot, routedKey) = await ApplySlotAffinityAsync(req, aff, root);
        }

        // Token Guard（仅 chat/completions）：预估算 + 裁剪，防 "exceeds context size" 400
        if (IsChatCompletions(p) && _cfg.TokenGuardEnabled && root != null)
        {
            var budget = _cfg.GetInputBudget(); // 多槽均分总容量：每槽有效上下文 = CtxSize ÷ Parallel；再减输出预留
            var (ok, _, note) = await TokenGuard.GuardAsync(root, _hc, _backendPort, budget); // Modified 无需：root 原地已改，末端统一序列化
            if (!ok)
            {
                Log?.Invoke($"Token Guard 拒绝：{note}");
                WriteError(ctx, 400, note ?? "上下文超长");
                return null;
            }
            if (note != null) Log?.Invoke(note);
        }

        // 非流式请求检测 + 可选强制流式改写：
        // 非流式时 llama-server 会缓存整个响应直到生成完毕才返回，期间无任何字节流动，
        // 客户端读超时→断开→agent 重试全量上下文→重新预填。流式则边生成边发字节，不会读超时。
        bool streaming;
        if (root != null)
        {
            // DOM 直读替代对数 MB body 的正则扫描（E-1）
            streaming = false;
            try { if (root["stream"]?.GetValue<bool>() == true) streaming = true; } catch { /* 非 bool 值：按 false */ }
        }
        else
            streaming = System.Text.RegularExpressions.Regex.IsMatch(body, @"""stream""\s*:\s*true");

        if (!streaming)
        {
            if (_cfg.ForceStream)
            {
                if (root != null)
                {
                    EnsureStreamTrue(root); // DOM 版：直接树上置 stream=true
                    Log?.Invoke("强制流式：已将非流式请求改写为 stream=true（SSE 直通）。");
                }
                else
                {
                    // C-005 降级：非法 JSON 走字符串级改写；改写失败透传原始请求，禁止下发损坏 JSON
                    var rewritten = EnsureStreamTrue(body);
                    if (rewritten != null)
                        bodyBytes = System.Text.Encoding.UTF8.GetBytes(rewritten);
                    Log?.Invoke("警告：强制流式改写失败（请求体不是合法 JSON），已透传原始请求。");
                }
            }
            else
            {
                WarnNonStreamOnce();
            }
        }

        // §8 可观测：前缀哈希 HIT/MISS 判定（原生 KV 前缀复用；TokenGuard 之后按实际下发体计算）
        if (routedKey != null)
            LogPrefixHash(routedKey, root);

        // 管道末端：唯一一次序列化 + 编码转换（E-1/E-3）
        if (root != null)
        {
            body = root.ToJsonString();
            bodyBytes = System.Text.Encoding.UTF8.GetBytes(body);
        }

        return (bodyBytes, body, streaming || _cfg.ForceStream, routedSlot, routedKey);
    }

    /// <summary>槽位亲和阶段：指纹绑定（LRU 驱逐 / §4.2 自动强占）→ §4.5 Tool 链锁定 → 驱逐前 KV save → restore 自愈 → n_slots 注入。
    /// E-1：直接操作调用方持有的同一棵 DOM（root=null 时跳过 DOM 步骤，等价旧实现 parse 失败透传）。
    /// 返回（路由槽位、绑定 key）。</summary>
    private async Task<(int? RoutedSlot, string? RoutedKey)> ApplySlotAffinityAsync(
        HttpListenerRequest req, SlotAffinity aff, JsonObject? root)
    {
        // §4.2 自动冻结：应用类型前缀在 AutoPreemptiveApps → 绑定强制强占（暂停 LRU 驱逐）
        var autoPre = ParseAutoPreemptivePrefixes();
        var (slot, key, isNew, evicted, evictedSlot, evictedKvCache) = aff.GetSlot(req.Headers, autoPre);
        int? routedSlot = slot;
        string? routedKey = key;

        // §4.5 Tool 链会话锁定：末条消息 role=tool → agent 工具循环进行中 → 锁槽位防驱逐；循环结束自动解锁
        if (key != null && root != null)
        {
            bool inToolLoop = DetectToolLoop(root);
            bool didLock = false, didUnlock = false;
            // O-15：锁内只做 _toolLockedKeys 集合判定；aff 调用（自带内部锁 + 文件 I/O）全部移出，消除锁嵌套
            bool alreadyPreemptive = aff.IsPreemptive(key);
            lock (_kvStateGate)
            {
                if (inToolLoop)
                {
                    if (!_toolLockedKeys.Contains(key) && !alreadyPreemptive)
                    {
                        _toolLockedKeys.Add(key);
                        didLock = true;
                    }
                }
                else if (_toolLockedKeys.Remove(key))
                {
                    didUnlock = true;
                }
            }
            if (didLock)
            {
                aff.SetPreemptive(key, true); // 移出锁外（O-15）
                EmitSlot($"[KV-LOCK] Tool 链会话锁定：{key} → slot{slot}（强占，不驱逐）");
            }
            else if (didUnlock)
            {
                aff.SetPreemptive(key, false);
                EmitSlot($"[KV-UNLOCK] Tool 链结束，解除锁定：{key}");
            }
        }

        var kv = _kvCache;

        // KV Cache：驱逐前 save（仅当被驱逐者的 KvCache=true；evicted != null 已蕴含 evictedSlot 有效，SlotAffinity 仅驱逐时置位）
        if (evicted != null && kv != null && evictedKvCache)
        {
            try
            {
                var saveTask = kv.SaveAsync(evictedSlot, evicted);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                await saveTask;
                EmitSlot($"KV Cache 保存：{evicted} → slot{evictedSlot}（{sw.Elapsed.TotalSeconds:F1}s）");
            }
            catch (Exception ex)
            {
                EmitSlot($"KV Cache 保存失败：{evicted}（{ex.Message}），降级为全量 prefill。");
            }
        }
        else if (evicted != null && !evictedKvCache)
        {
            EmitSlot($"驱逐 {evicted}（KV Cache 已关闭，不保存）");
        }

        // KV Cache：restore（两种触发：① isNew 重绑定；② 进程重启后该 key 首次使用——休眠唤醒 KV 自愈。
        // 无论是否命中 restore，都把 key 记入 _servedKeysThisRun：本进程服务过即不再 restore，防误用磁盘旧快照回退内存新状态）
        if (key != null)
        {
            bool firstUseThisRun;
            lock (_kvStateGate) firstUseThisRun = _servedKeysThisRun.Add(key);
            if (kv != null && kv.HasCache(key) && (isNew || firstUseThisRun))
            {
                try
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    bool ok = await kv.RestoreAsync(slot, key);
                    if (ok)
                    {
                        EmitSlot($"[KV-RESTORE] KV Cache 恢复：{key} → slot{slot}（{sw.Elapsed.TotalSeconds:F1}s，跳过全量 prefill）");
                        // §8：restore 后重建前缀哈希基线（旧哈希对应驱逐前状态，避免下次请求误报 MISS）
                        lock (_kvStateGate) _prefixHashes.Remove(key);
                    }
                    else
                    {
                        EmitSlot($"KV Cache 恢复失败：{key}（槽位可能忙），降级为全量 prefill。");
                    }
                }
                catch (Exception ex)
                {
                    EmitSlot($"KV Cache 恢复异常：{key}（{ex.Message}），降级为全量 prefill。");
                }
            }
        }

        if (isNew)
        {
            var evt = $"槽位绑定：{key} → slot{slot}{(evicted != null ? $"（驱逐 {evicted}）" : "")}";
            EmitSlot(evt);
            SlotBindingChanged?.Invoke();
        }
        // E-1：n_slots 注入直接改树（已有 n_slots 时不覆盖，尊重客户端显式指定）
        if (root != null)
            InjectNSlots(root, slot);
        return (routedSlot, routedKey);
    }

    /// <summary>转发阶段：构造后端请求（过滤逐跳头）→ 连接异常 500ms 重试一次 → 响应管道 → 崩溃恢复 / 断点快照清理 → 客户端断开兜底。</summary>
    private async Task SendAndPipeAsync(
        HttpListenerContext ctx, Uri uri, string path, HttpListenerRequest req,
        byte[]? bodyBytes, string? finalBody, bool effStreaming, int? routedSlot, string? routedKey)
    {
        using var msg = BuildBackendRequest(req, uri, bodyBytes);

        HttpResponseMessage resp;
        try
        {
            resp = await _hc.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead);
        }
        catch (HttpRequestException)
        {
            // 连接层瞬时失败（后端刚重启 / 连接被重置）：稍等后重试一次
            Log?.Invoke("转发连接异常，正在重试…");
            await Task.Delay(500);
            resp = await _hc.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead);
        }
        using (resp)
        {
            var outResp = ctx.Response;
            outResp.StatusCode = (int)resp.StatusCode;
            var ct = resp.Content.Headers.ContentType?.ToString();
            outResp.ContentType = string.IsNullOrEmpty(ct) ? "application/octet-stream" : ct!;
            try
            {
                (bool completed, string accumulated) = await PipeResponseAsync(
                    resp, outResp, uri, path, finalBody, effStreaming, routedSlot, routedKey);

                // 崩溃恢复：流中断/5xx bad_alloc → keep-alive 保活 + KV 快照接续 / 进程重启全量重放
                if (!completed && _cfg.CrashRecoveryEnabled && effStreaming && finalBody != null)
                {
                    var log2 = (string s) => Log?.Invoke(s);
                    await TryCrashRecoverAsync(uri, outResp, finalBody, accumulated, routedSlot, routedKey, log2);
                }

                // §6.3：续接成功 → 清理过期断点快照（槽活 KV 已领先断点，旧快照 restore 会回退状态）；失败则保留供下次 rebinding/崩溃恢复 restore
                if (completed && routedKey != null)
                {
                    bool wasPending;
                    lock (_kvStateGate) wasPending = _truncPending.Remove(routedKey);
                    if (wasPending)
                    {
                        try
                        {
                            _kvCache?.DeleteCache(routedKey);
                            Log?.Invoke($"[KV-CLEANUP] 续接成功，清理过期断点快照：{routedKey}");
                        }
                        catch { /* 清理失败不影响主流程 */ }
                    }
                }
            }
            catch (Exception)
            {
                // 客户端断开/写入失败：方法退出时 dispose resp 关闭后端连接，
                // llama-server 检测到断开会取消任务并保留部分槽位 KV（f_keep），释放 GPU。
                // 多 agent 模式下这是预期行为（agent 超时/重试），非致命错误。
                Log?.Invoke("客户端断开，已中止本次生成（多 agent 下属正常重试）。");
            }
            finally
            {
                outResp.Close();
            }
        }
    }

    /// <summary>构造后端 HttpRequestMessage：body 走内容头，Host/长度/编码等逐跳头由 HttpClient 处理，其余原样复制（个别特殊头复制失败忽略）。</summary>
    private static HttpRequestMessage BuildBackendRequest(HttpListenerRequest req, Uri uri, byte[]? bodyBytes)
    {
        var msg = new HttpRequestMessage(new HttpMethod(req.HttpMethod), uri);
        if (bodyBytes != null)
        {
            msg.Content = new ByteArrayContent(bodyBytes);
            // Content-Type 走内容头，避免与消息级头重复
            if (!string.IsNullOrEmpty(req.ContentType))
                msg.Content.Headers.ContentType = new MediaTypeHeaderValue(req.ContentType);
        }
        foreach (string key in req.Headers)
        {
            if (key.Equals("Host", StringComparison.OrdinalIgnoreCase)) continue;
            if (key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
            if (key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)) continue;
            if (key.Equals("Connection", StringComparison.OrdinalIgnoreCase)) continue;
            if (key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) continue; // 已在内容头上显式设置
            try
            {
                msg.Headers.TryAddWithoutValidation(key, req.Headers[key]);
            }
            catch
            {
                // 个别特殊头无法原样复制，忽略
            }
        }
        return msg;
    }

    /// <summary>响应管道：chat/completions 走输出续接 + 崩溃识别（截断断点快照闭包 / 5xx bad_alloc 判定），其余透传。</summary>
    /// 返回 (是否完整完成, 已累积输出文本)。</summary>
    private async Task<(bool Completed, string Accumulated)> PipeResponseAsync(
        HttpResponseMessage resp, HttpListenerResponse outResp, Uri uri, string path,
        string? finalBody, bool effStreaming, int? routedSlot, string? routedKey)
    {
        if (!(IsChatCompletions(path) && finalBody != null))
        {
            await resp.Content.CopyToAsync(outResp.OutputStream);
            return (true, "");
        }

        // 输出续接 + 崩溃识别：finish_reason=length 自动续接；流中断/5xx bad_alloc → Completed=false
        var log2 = (string s) => Log?.Invoke(s);

        // §4.1 截断断点快照闭包：finish_reason=length 时、续接请求发出前 save 槽位 KV（此时槽位 KV 仍完整）
        Func<Task>? onTrunc = null;
        var kvForTrunc = _kvCache;
        if (kvForTrunc != null && routedSlot is int truncSlot && !string.IsNullOrEmpty(routedKey))
        {
            var truncKey = routedKey;
            onTrunc = async () =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                await kvForTrunc.SaveAsync(truncSlot, truncKey);
                EmitSlot($"[KV-SAVE] 截断断点快照：{truncKey} → slot{truncSlot}（{sw.Elapsed.TotalSeconds:F1}s）");
                lock (_kvStateGate) _truncPending.Add(truncKey); // 标记「截断待续接」
            };
        }

        bool completed;
        string accumulated = ""; // bad_alloc 崩溃路径无输出累积（保持与原实现一致的初始值）
        if (resp.IsSuccessStatusCode)
        {
            if (effStreaming)
            {
                // SSE 流式响应：必须设置 text/event-stream（llama-server 返回 application/json，
                // 直接复制会导致客户端按 JSON 解析 SSE 行报错 "Unexpected non-whitespace character after JSON"）
                outResp.ContentType = "text/event-stream";
                (completed, accumulated) = await OutputContinuer.HandleStreamAsync(_hc, uri, _backendPort, finalBody, resp, outResp, _cfg, log2, onTrunc);
            }
            else
                (completed, accumulated) = await OutputContinuer.HandleNonStreamAsync(_hc, uri, _backendPort, finalBody, resp, outResp, _cfg, log2, _cfg.CrashRecoveryEnabled);
        }
        else
        {
            // 5xx 错误响应：判定是否 bad_alloc 崩溃（恢复启用 → 不转发，交给崩溃恢复）
            string errBody = System.Text.Encoding.UTF8.GetString(await resp.Content.ReadAsByteArrayAsync());
            bool isBadAlloc = errBody.Contains("bad allocation", StringComparison.OrdinalIgnoreCase)
                             || CrashRecovery.WasBadAlloc(BadAllocEvidenceWindow);
            if (isBadAlloc && _cfg.CrashRecoveryEnabled && effStreaming)
            {
                completed = false; // 交给 TryCrashRecoverAsync
            }
            else
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(errBody);
                outResp.ContentType = "application/json";
                outResp.ContentLength64 = bytes.Length;
                await outResp.OutputStream.WriteAsync(bytes);
                completed = true;
            }
        }
        return (completed, accumulated);
    }

    // ==================== KV 全场景复用辅助（§4.2/§4.5/§8） ====================

    /// <summary>解析 AutoPreemptiveApps 配置为前缀集合（§4.2 自动冻结）。</summary>
    private List<string> ParseAutoPreemptivePrefixes()
    {
        return _cfg.AutoPreemptiveApps.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
    }

    /// <summary>§4.5 Tool 链检测：messages 末条 role=tool → agent 工具循环进行中（框架刚回填 tool_result、等待模型响应）。
    /// 历史中残留的旧 tool 消息不作为依据（循环结束后历史仍含 tool 消息，全量扫描会永久误锁）。
    /// E-1：直接读调用方持有的 DOM，不再单独 parse。</summary>
    public static bool DetectToolLoop(JsonObject obj)
    {
        try
        {
            var msgs = obj["messages"] as System.Text.Json.Nodes.JsonArray;
            if (msgs == null || msgs.Count == 0) return false;
            return string.Equals(msgs[^1]?["role"]?.GetValue<string>(), "tool", StringComparison.OrdinalIgnoreCase); // 与 InjectThinkingMode 的 role 比较口径一致
        }
        catch
        {
            return false;
        }
    }

    /// <summary>前缀指纹（E-4 轻量版）：消息条数 + 各条 role|content长度 序列，零全量序列化、零 SHA256。
    /// 旧实现对除末条外全部 messages 做 ToJsonString + SHA256（大上下文每请求数 MB 开销），仅用于 [KV-HIT]/[KV-MISS] 日志判定；
    /// 轻量指纹的碰撞概率对该场景可接受（误 HIT 只影响日志，不影响实际 KV 行为）。null = 无状态单轮请求（无比对基线）。</summary>
    public static string? PrefixHash(JsonObject obj)
    {
        try
        {
            var msgs = obj["messages"] as System.Text.Json.Nodes.JsonArray;
            if (msgs == null || msgs.Count < 2) return null;
            // 指纹形如 "12:user|1834,assistant|92,..."（条数 + 除末条外各条 role|content长度）
            var sb = new StringBuilder(msgs.Count * 24);
            sb.Append(msgs.Count);
            for (int i = 0; i < msgs.Count - 1; i++)
            {
                var m = msgs[i]?.AsObject();
                var role = m?["role"]?.GetValue<string>() ?? "?";
                sb.Append(',').Append(role).Append('|').Append(ContentLen(m));
            }
            return sb.ToString();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>消息 content 长度（string = 字符数；数组型 = 序列化长度；无 = 0）。仅用于轻量指纹。</summary>
    private static int ContentLen(JsonObject? m)
    {
        var c = m?["content"];
        if (c == null) return 0;
        try
        {
            return c.GetValue<string>()?.Length ?? 0;
        }
        catch
        {
            return c.ToJsonString().Length; // 数组型 content：序列化长度作口径
        }
    }

    /// <summary>§8 可观测：前缀哈希 HIT/MISS 判定。一致 → 原生 KV 前缀复用（增量 prefill）；不一致 → 全量重算。</summary>
    private void LogPrefixHash(string key, JsonObject? root)
    {
        var hash = root != null ? PrefixHash(root) : null;
        if (hash == null) return;
        lock (_kvStateGate)
        {
            if (_prefixHashes.TryGetValue(key, out var prev))
            {
                if (prev == hash)
                    Log?.Invoke($"[KV-HIT] {key}：前缀未变 → 原生 KV 复用（增量 prefill）");
                else
                    Log?.Invoke($"[KV-MISS] {key}：前缀变更 → 全量重算");
            }
            _prefixHashes[key] = hash;
        }
    }

    // ==================== 崩溃自动恢复（bad_alloc） ====================

    /// <summary>
    /// bad_alloc 崩溃自动恢复管道（三分支）：
    /// - 分支 A（服务端存活 + 客户端连接可持有）：抢 save 槽位 KV 快照 → SSE keep-alive 保活客户端
    ///   → 内存余量检查 → 快照接续（restore + 回填已生成部分 + 续接指令）或全量重放（严格预算）→ 输出灌入同一条流（客户端无感）
    /// - 分支 B（进程死亡）：重启至多 MaxAutoRestarts 次并等就绪 → 严格预算全量重放（无快照）
    /// - 分支 C（客户端已断开）：不重放；agent 侧重试走现有 KV restore 路径
    /// 熔断器：10 分钟窗口内 ≥3 次确认崩溃 → 停止自动恢复，醒目报错，等待人工介入。
    /// </summary>
    private async Task TryCrashRecoverAsync(
        Uri uri, HttpListenerResponse outResp, string finalBody, string accumulated,
        int? routedSlot, string? routedKey, Action<string>? log)
    {
        // ── 诊断增强：崩溃瞬间记录系统资源（判定主机 RAM 还是显存打满 → 长期方案：降 ctx / 换 mmap / 加内存）──
        var m = new SystemMetrics();
        var (usedGb, totalGb) = m.GetMemory();
        double freeGb = totalGb - usedGb;
        int? vramUsedMb = await SystemMetrics.GetVramUsedMbAsync();
        log?.Invoke($"崩溃恢复触发。崩溃时刻诊断：空闲 RAM {freeGb:F1}/{totalGb:F1} GB，显存 {(vramUsedMb is int v ? $"{v} MB" : "未知")}");

        // ── 熔断器：10 分钟窗口内 ≥3 次确认崩溃 → 停止自动恢复（需人工介入）──
        CrashRecovery.RecordCrash();
        if (!CrashRecovery.AllowRecover())
        {
            log?.Invoke($"熔断器已跳闸：10 分钟内 {CrashRecovery.ConfirmedCount} 次崩溃 ≥ {CrashRecovery.MaxCrashesInWindow}，停止自动恢复。请加内存 / 降上下文后手动重试。");
            RaiseStatus("⚠ 崩溃熔断：自动恢复已停止，需人工介入");
            return;
        }

        // 分支 C（客户端已断开）由各分支内的探测写判定：立即写一行 keep-alive，写失败 = 客户端已断开 → 不重放。

        if (_server.IsRunning)
            await RecoverAliveAsync(uri, outResp, finalBody, accumulated, routedSlot, routedKey, freeGb, log);
        else
            await RestartAndReplayAsync(uri, outResp, finalBody, log);
    }

    /// <summary>分支 A：服务端存活 + 客户端连接可持有 → 抢 save 快照（抢在 release 前）→ 内存余量检查 → 快照接续或全量重放。
    /// keep-alive 保活 / 分支 C 探测 / 异常兜底由 RunCrashRecoveryAsync 公共骨架提供（审计 O-10）。</summary>
    private Task RecoverAliveAsync(
        Uri uri, HttpListenerResponse outResp, string finalBody, string accumulated,
        int? routedSlot, string? routedKey, double freeGb, Action<string>? log)
        => RunCrashRecoveryAsync(outResp, log, async writeGate =>
        {
            // ── 抢 save 槽位 KV（llama.cpp 崩溃即 release 槽位；抢到 n_saved>0 = 有效快照，否则全量路径）──
            var kv = _kvCache;
            bool snapshotOk = false;
            if (kv != null && routedSlot is int slot && !string.IsNullOrEmpty(routedKey))
            {
                try
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    await kv.SaveAsync(slot, routedKey);
                    int nSaved = kv.SavedTokens(routedKey);
                    if (nSaved > 0)
                    {
                        snapshotOk = true;
                        log?.Invoke($"崩溃快照抢获：{routedKey} → slot{slot}（{sw.Elapsed.TotalSeconds:F1}s，{nSaved} tokens）");
                    }
                    else
                    {
                        log?.Invoke("崩溃快照为空（槽位已 release，n_saved=0）：降级全量重放路径。");
                    }
                }
                catch (Exception ex)
                {
                    log?.Invoke($"崩溃快照保存失败：{ex.Message}，降级全量重放路径。");
                }
            }

            // ── 内存余量检查：空闲 RAM < 4GB → 预算收紧 25%（防同点再崩）──
            bool tightBudget = freeGb < TightMemoryFreeGb;
            int budget = _cfg.GetInputBudget();
            if (tightBudget)
            {
                budget = Math.Max(AppConfig.MinInputBudgetTokens, (int)(budget * TightBudgetFactor));
                log?.Invoke($"内存余量不足（空闲 {freeGb:F1} GB < {TightMemoryFreeGb} GB）：重放预算收紧 25% 防再崩。");
            }

            string? replayBody = null;
            bool usedSnapshot = false; // 实际走快照接续路径的标志（末行日志准确反映路径）

            // ── 快照接续：restore 快照 + 回填 assistant（已生成部分）+ 续接指令 ──
            if (snapshotOk && kv != null && routedSlot is int slot2 && !string.IsNullOrEmpty(routedKey))
            {
                bool restored = false;
                try { restored = await kv.RestoreAsync(slot2, routedKey); }
                catch (Exception ex) { log?.Invoke($"快照 restore 异常：{ex.Message}"); }

                if (restored)
                {
                    // accumulated 为空（prefill 阶段崩溃无输出）→ 不构造空 assistant 续接体，原请求直接重放（restore 的 KV 供前缀复用）
                    string? contBody = string.IsNullOrEmpty(accumulated)
                        ? null
                        : OutputContinuer.BuildContinuationBody(finalBody, accumulated);
                    bool useSnapshot = contBody != null || string.IsNullOrEmpty(accumulated);
                    if (!useSnapshot)
                        log?.Invoke("续接体构造失败：降级全量重放路径。");

                    if (useSnapshot)
                    {
                        var target = contBody ?? finalBody;
                        var (ok, guarded, note) = await TokenGuard.GuardAsync(_hc, _backendPort, target, budget);
                        if (!ok)
                        {
                            log?.Invoke($"续接中止：{note}（内存余量不足且上下文无法裁剪）。");
                            return; // 中止并明确报错（客户端流结束，agent 侧重试走现有机制）
                        }
                        if (note != null) log?.Invoke(note);
                        replayBody = guarded ?? target;
                        usedSnapshot = true;
                    }
                }
                else
                {
                    log?.Invoke("快照 restore 失败（槽位忙？）：降级全量重放路径。");
                }
            }

            // ── 全量重放路径（无快照 / restore 失败）：严格预算 TokenGuard 裁剪 + 原请求重发 ──
            if (replayBody == null)
            {
                var (ok, guarded, note) = await TokenGuard.GuardAsync(_hc, _backendPort, finalBody, budget);
                if (!ok)
                {
                    log?.Invoke($"重放中止：{note}（内存余量不足且上下文无法裁剪）。");
                    return;
                }
                if (note != null) log?.Invoke(note);
                replayBody = guarded ?? finalBody;
            }

            log?.Invoke(usedSnapshot ? "崩溃快照接续：restore KV + 回填已生成部分 + 续接指令…" : "全量重放：原请求重发（严格预算）…");
            var (replayCompleted, _) = await OutputContinuer.SendAndPipeStreamAsync(_hc, uri, _backendPort, replayBody, outResp, _cfg, log, writeGate);
            if (!replayCompleted)
                log?.Invoke("重放流再次中断（二次崩溃？）：本次恢复失败，agent 侧重试将走现有机制。");
        });

    /// <summary>崩溃恢复公共骨架（审计 O-10：收敛 A/B 分支重复的 keep-alive 启动 + 分支 C 探测 + 异常兜底 + 收尾样板）：
    /// 立即启动 SSE keep-alive（保活客户端）→ 探测客户端连接（断开即放弃重放）→ 执行分支体 → 统一异常兜底与 keep-alive 收尾。</summary>
    private async Task RunCrashRecoveryAsync(HttpListenerResponse outResp, Action<string>? log, Func<SemaphoreSlim, Task> body)
    {
        // ── SSE keep-alive（立即启动：从崩溃检测时刻起保活客户端，Trae 看到停顿后继续出字）──
        var keepAliveCts = new CancellationTokenSource();
        var writeGate = new SemaphoreSlim(1, 1); // 写门控：keep-alive 与重放管道并发写互斥，防 SSE 行交错
        Task keepAliveTask = RunKeepAliveAsync(outResp, writeGate, keepAliveCts.Token, log);
        try
        {
            // ── 分支 C 探测：客户端已断开 → 不重放（agent 侧重试走现有 KV restore 路径）──
            if (!await ProbeClientConnectedAsync(outResp, writeGate))
            {
                log?.Invoke("客户端已断开：跳过重放（agent 侧重试将走现有 KV restore 路径）。");
                return;
            }
            await body(writeGate);
        }
        catch (Exception ex)
        {
            log?.Invoke($"崩溃恢复异常：{ex.Message}");
        }
        finally
        {
            keepAliveCts.Cancel();
            try { await keepAliveTask; } catch { } // 等在途 keep-alive 写入完成再返回（调用方负责关连接）
        }
    }

    /// <summary>分支 B：进程死亡 → 重启至多 MaxAutoRestarts 次并等就绪 → 严格预算全量重放（无快照，防同点再崩）。
    /// keep-alive 保活 / 分支 C 探测 / 异常兜底由 RunCrashRecoveryAsync 公共骨架提供（审计 O-10）。</summary>
    private Task RestartAndReplayAsync(Uri uri, HttpListenerResponse outResp, string finalBody, Action<string>? log)
        => RunCrashRecoveryAsync(outResp, log, async writeGate =>
        {
            int maxRestarts = Math.Max(0, _cfg.MaxAutoRestarts);
            if (maxRestarts == 0)
            {
                log?.Invoke("进程已死且 MaxAutoRestarts=0（自动重启禁用）：无法自动恢复，请手动启动。");
                return;
            }

            bool restarted = false;
            for (int attempt = 1; attempt <= maxRestarts && !restarted; attempt++)
            {
                log?.Invoke($"崩溃恢复：重启 llama-server（{attempt}/{maxRestarts}）…");
                RaiseStatus($"崩溃恢复：正在重启后端服务（{attempt}/{maxRestarts}）…");
                try
                {
                    await EnsureRunningAsync();
                    restarted = true;
                }
                catch (Exception ex)
                {
                    log?.Invoke($"重启失败（{attempt}/{maxRestarts}）：{ex.Message}");
                }
            }

            if (!restarted)
            {
                log?.Invoke("全部重启失败：无法自动恢复，请手动启动。");
                return;
            }

            // 重启后后端端口可能变化（自动探测空闲端口），重建 URI
            var replayUri = new Uri($"http://localhost:{_backendPort}{uri.AbsolutePath}{uri.Query}");

            // 严格预算全量重放（无快照）：重启后内存状态未知，统一收紧 25% 防同点再崩
            int budget = Math.Max(AppConfig.MinInputBudgetTokens, (int)(_cfg.GetInputBudget() * TightBudgetFactor));
            var (ok, guarded, note) = await TokenGuard.GuardAsync(_hc, _backendPort, finalBody, budget);
            if (!ok)
            {
                log?.Invoke($"重放中止：{note}（上下文无法裁剪到严格预算）。");
                return;
            }
            if (note != null) log?.Invoke(note);

            log?.Invoke("全量重放：原请求重发（严格预算，无快照）…");
            var (replayCompleted, _) = await OutputContinuer.SendAndPipeStreamAsync(_hc, replayUri, _backendPort, guarded ?? finalBody, outResp, _cfg, log, writeGate);
            if (!replayCompleted)
                log?.Invoke("重放流再次中断：本次恢复失败。");
        });

    /// <summary>探测客户端连接是否存活：立即写一行 keep-alive 注释；写失败 = 客户端已断开（分支 C）。</summary>
    private static async Task<bool> ProbeClientConnectedAsync(HttpListenerResponse outResp, SemaphoreSlim writeGate)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(": keepalive\n");
        await writeGate.WaitAsync();
        try
        {
            await outResp.OutputStream.WriteAsync(bytes);
            await outResp.OutputStream.FlushAsync();
            return true;
        }
        catch
        {
            return false; // 写入失败 = 客户端已断开
        }
        finally
        {
            writeGate.Release();
        }
    }

    /// <summary>SSE keep-alive：每 N 秒写一行注释（客户端忽略但连接不断），直到取消或客户端断开。</summary>
    private async Task RunKeepAliveAsync(HttpListenerResponse outResp, SemaphoreSlim writeGate, CancellationToken ct, Action<string>? log)
    {
        var intervalSec = Math.Max(1, _cfg.RecoveryKeepAliveIntervalSeconds);
        var bytes = System.Text.Encoding.UTF8.GetBytes(": keepalive\n");
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(intervalSec), ct);
                await writeGate.WaitAsync(ct);
                try
                {
                    await outResp.OutputStream.WriteAsync(bytes);
                    await outResp.OutputStream.FlushAsync();
                }
                finally
                {
                    writeGate.Release();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止（恢复流程完成）
        }
        catch (Exception ex)
        {
            log?.Invoke($"keep-alive 停止：{ex.Message}");
        }
    }

    /// <summary>请求体 dump（应用识别分析用）：原始 body + headers 落盘到 request_dump.log。</summary>
    private void DumpRequest(HttpListenerContext ctx, byte[] bodyBytes)
    {
        try
        {
            var req = ctx.Request;
            var ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            var path = req.Url?.AbsolutePath ?? "";
            var bodyStr = System.Text.Encoding.UTF8.GetString(bodyBytes);

            var headers = new StringBuilder();
            foreach (var key in req.Headers.AllKeys)
            {
                headers.AppendLine($"{key}: {req.Headers[key]}");
            }

            // 请求体截断（DumpBodyMaxLength 字符）：避免日志爆炸，system prompt 通常在前部
            if (bodyStr.Length > DumpBodyMaxLength)
                bodyStr = bodyStr.Substring(0, DumpBodyMaxLength) + $"...(truncated, total {System.Text.Encoding.UTF8.GetByteCount(bodyStr)} bytes)";

            var dumpLine = $"[{ts}] POST {path}\n--- Headers ---\n{headers}--- Body ---\n{bodyStr}\n{new string('=', 80)}\n\n";
            lock (_dumpLock)
            {
                var logDir = System.IO.Path.Combine(AppContext.BaseDirectory, "logs");
                if (!System.IO.Directory.Exists(logDir)) System.IO.Directory.CreateDirectory(logDir);
                using var sw = new StreamWriter(new FileStream(
                    System.IO.Path.Combine(logDir, "request_dump.log"),
                    FileMode.Append, FileAccess.Write));
                sw.Write(dumpLine);
            }
        }
        catch { /* dump 失败不影响主流程 */ }
    }

    private readonly object _dumpLock = new(); // 实例字段：与其余锁风格统一（单实例调度器，无需 static）

    /// <summary>判断是否为真实推理请求（刷新闲置计时）：POST + completions/embeddings 路径。</summary>
    private static bool IsInferenceRequest(HttpListenerRequest req)
    {
        if (!string.Equals(req.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase)) return false;
        var p = req.Url?.AbsolutePath ?? "";
        return p.Contains("completion", StringComparison.OrdinalIgnoreCase)
               || p.Contains("embedding", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>判断是否为 chat/completions 推理请求（思考模式注入仅对此类请求生效）。</summary>
    private static bool IsChatCompletions(string path)
    {
        return path.Contains("/chat/completions", StringComparison.OrdinalIgnoreCase);
    }

    private int _nonStreamWarned; // 每会话只告警一次，唤醒时重置

    /// <summary>非流式推理请求告警（每会话一次）：非流式是"断开→全量重填"循环的常见诱因。</summary>
    private void WarnNonStreamOnce()
    {
        if (Interlocked.Increment(ref _nonStreamWarned) == 1)
            Log?.Invoke("警告：检测到非流式推理请求。llama-server 会阻塞整个生成后才返回，客户端读超时可能触发断开→重试全量重新预填。" +
                        "建议：Agent 侧启用流式（stream=true）或加大请求超时；也可在启动器开启「强制流式」。");
    }

    /// <summary>E-1 DOM 版：把非流式请求体改写为 stream=true——直接在树上置位（热路径复用同一棵树，无 parse/serialize）。</summary>
    public static void EnsureStreamTrue(JsonObject obj) => obj["stream"] = true;

    /// <summary>字符串降级版：仅当入口解析失败（root=null）时用于 C-005 兜底改写。
    /// C-005：优先 System.Text.Json DOM 解析修改（正确处理字符串内含 '}'、注释、格式化 JSON）；
    /// DOM 失败回退字符串 hack；两者都失败返回 null，调用方透传原始请求。</summary>
    public static string? EnsureStreamTrue(string json)
    {
        try
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(json);
            if (node is System.Text.Json.Nodes.JsonObject obj)
            {
                obj["stream"] = true;
                return obj.ToJsonString();
            }
        }
        catch
        {
            // DOM 解析失败（非法 JSON），走字符串降级
        }
        // 降级：字符串级修改（"stream":false 直接替换；无 stream 字段注入到最后一个 '}' 前）
        if (System.Text.RegularExpressions.Regex.IsMatch(json, @"""stream""\s*:\s*false"))
            return System.Text.RegularExpressions.Regex.Replace(json, @"""stream""\s*:\s*false", @"""stream"":true");
        int idx = json.LastIndexOf('}');
        if (idx <= 0) return null;
        var prefix = json.Substring(0, idx).TrimEnd();
        bool hasComma = prefix.EndsWith(',');
        string field = "\"stream\":true";
        return $"{json.Substring(0, idx)}{(hasComma ? "" : ",")}{field}{json.Substring(idx)}";
    }

    /// <summary>
    /// 思考模式拦截与注入（仅 chat/completions POST 请求体）：
    /// 1. 检测 messages 数组最后一条 user 消息是否含思考/推理指令：
    ///    - 「开启思考模式」→ XHigh（未指定深度时默认深度档）；
    ///    - 「关闭思考模式」→ Off；
    ///    - 「开启轻度推理模式」→ Low；「开启中度推理模式」→ Medium；「开启深度推理模式」→ XHigh。
    ///    命中 → 设置全局档位，剥离指令文本（避免模型把指令当问题回答）。
    /// 2. 统一清洗：移除请求体中客户端自带的 chat_template_kwargs.reasoning_effort / enable_thinking
    ///    （网关代理层统一管控思考参数，不信任客户端自行携带的值）。
    /// 3. 按状态机注入：Off → 显式 enable_thinking=false（Qwen3 混合思考模型默认会思考，
    ///    不显式关闭则仍输出 reasoning_content，导致下游 pi-ai 严格 JSON.parse 报 PI_AI_ERROR）；
    ///    Low/Medium/XHigh → 注入对应 reasoning_effort + enable_thinking=true。
    /// E-1 DOM 版：原地改树，复用调用方持有的同一棵树（无 parse/serialize）。
    /// </summary>
    /// <param name="obj">请求体 DOM（入口一次性解析）</param>
    /// <param name="level">当前全局思考档位（ref：指令命中时更新）</param>
    /// <param name="effortFix">清洗/注入描述（如 "已清洗客户端 reasoning_effort=high"）；null = 无需说明</param>
    public static void InjectThinkingMode(JsonObject obj, ref ThinkingLevel level, out string? effortFix)
    {
        effortFix = null;
        // 注意：不再有无改动的快速路径——Off 态也必须显式注入 enable_thinking=false，
        // 否则 Qwen3 混合思考模型默认仍会输出 reasoning_content（实测 REASONING_LEN≈5800），
        // 思考文本混入 tool-call JSON 后导致 pi-ai 严格 JSON.parse 报 PI_AI_ERROR。
        try
        {
            if (obj["messages"] is System.Text.Json.Nodes.JsonArray msgs && msgs.Count > 0)
            {
                for (int i = msgs.Count - 1; i >= 0; i--)
                {
                    if (msgs[i] is not System.Text.Json.Nodes.JsonObject msgObj) continue;
                    // role 提取：JsonNode.ToString() 对字符串节点返回不带引号的原始值
                    string? roleStr = msgObj["role"]?.ToString();
                    if (!string.Equals(roleStr, "user", StringComparison.OrdinalIgnoreCase)) break;

                    // content 提取：仅处理字符串类型（数组/对象跳过）
                    var contentNode = msgObj["content"];
                    if (contentNode == null) continue;
                    // AsObject() 对非对象节点抛异常，用 try-catch 安全判断
                    bool isContainer = false;
                    try { isContainer = contentNode.AsObject() != null || contentNode.AsArray() != null; } catch { }
                    if (isContainer) continue;
                    string contentStr = contentNode.ToString();

                    bool hitOn = contentStr.Contains("开启思考模式");
                    bool hitOff = contentStr.Contains("关闭思考模式");
                    bool hitLow = contentStr.Contains("开启轻度推理模式");
                    bool hitMid = contentStr.Contains("开启中度推理模式");
                    bool hitDeep = contentStr.Contains("开启深度推理模式");
                    if (!hitOn && !hitOff && !hitLow && !hitMid && !hitDeep) continue;

                    // 剥离全部命中指令，保留其余内容；若消息只剩指令本身，填确认提示避免空消息让模型困惑
                    string stripped = contentStr;
                    if (hitOn) { level = ThinkingLevel.XHigh; stripped = stripped.Replace("开启思考模式", ""); }
                    if (hitOff) { level = ThinkingLevel.Off; stripped = stripped.Replace("关闭思考模式", ""); }
                    if (hitLow) { level = ThinkingLevel.Low; stripped = stripped.Replace("开启轻度推理模式", ""); }
                    if (hitMid) { level = ThinkingLevel.Medium; stripped = stripped.Replace("开启中度推理模式", ""); }
                    if (hitDeep) { level = ThinkingLevel.XHigh; stripped = stripped.Replace("开启深度推理模式", ""); }
                    msgObj["content"] = string.IsNullOrWhiteSpace(stripped.Trim())
                        ? "（思考/推理模式已切换，请简短确认）"
                        : stripped.Trim();
                    break;
                }
            }

            // 2. 统一清洗：移除客户端自带的思考相关字段（网关层统一管控）
            // DSH 客户端发送的思考字段：顶层 "thinking" / "reasoning_effort" + chat_template_kwargs 内字段
            bool cleaned = false;
            // 顶层字段（DSH 格式）
            if (obj.Remove("thinking")) cleaned = true;
            if (obj.Remove("reasoning_effort")) cleaned = true;
            // chat_template_kwargs 内字段（部分客户端格式）
            if (obj["chat_template_kwargs"] is System.Text.Json.Nodes.JsonObject ctkExisting)
            {
                if (ctkExisting.Remove("reasoning_effort")) cleaned = true;
                if (ctkExisting.Remove("enable_thinking")) cleaned = true;
                // 清洗后若 chat_template_kwargs 为空对象，移除空壳（避免下发无意义字段）
                if (ctkExisting.Count == 0) obj.Remove("chat_template_kwargs");
            }

            // 3. 按状态机注入：Off → 显式 enable_thinking=false；Low/Medium/XHigh → reasoning_effort + enable_thinking=true
            System.Text.Json.Nodes.JsonObject ctk;
            if (obj["chat_template_kwargs"] is System.Text.Json.Nodes.JsonObject existing)
            {
                ctk = existing;
            }
            else
            {
                ctk = new System.Text.Json.Nodes.JsonObject();
                obj["chat_template_kwargs"] = ctk;
            }
            if (level == ThinkingLevel.Off)
            {
                ctk["enable_thinking"] = false; // 关键：混合思考模型必须显式关闭，否则默认仍思考
            }
            else
            {
                ctk["reasoning_effort"] = EffortOf(level);
                ctk["enable_thinking"] = true;
            }

            // 清洗说明（用于日志）
            if (cleaned)
                effortFix = "已清洗客户端思考参数（thinking/reasoning_effort/enable_thinking），按网关状态机重新注入";
        }
        catch
        {
            // 结构异常：尽力而为，保留已完成的改写（等价旧实现透传语义）
        }
    }

    /// <summary>注入 n_slots 固定槽位路由（llama.cpp 多槽特性）。E-1 DOM 版：原地改树。
    /// 已有 n_slots 时不覆盖（尊重客户端显式指定），返回 false。</summary>
    public static bool InjectNSlots(JsonObject obj, int slot)
    {
        if (obj["n_slots"] != null) return false;
        obj["n_slots"] = new System.Text.Json.Nodes.JsonArray(slot);
        return true;
    }

    // ==================== 闲置休眠（15 分钟无请求自动释放） ====================

    /// <summary>刷新闲置倒计时基准点（Interlocked 原子写，供多线程读取）。</summary>
    private void Touch() => Interlocked.Exchange(ref _lastTouchTicks, DateTime.Now.Ticks);

    private void OnTick(object? _)
    {
        if (CurrentPhase != Phase.Running) return;
        int inflight = Volatile.Read(ref _inflight);
        var remaining = new DateTime(Interlocked.Read(ref _lastTouchTicks)).Add(TimeSpan.FromMinutes(IdleMinutes)) - DateTime.Now;
        if (remaining <= TimeSpan.Zero && inflight == 0)
            SleepNow();
        else if (inflight > 0)
            // 有在途任务时不触发休眠，明确提示原因（长驻 SSE 流式连接会一直压制休眠）
            RaiseStatus($"运行中 · {inflight} 个在途任务，休眠暂停");
        else
            RaiseStatus($"运行中 · {(int)remaining.TotalMinutes:D2}:{remaining.Seconds:D2} 无请求后自动休眠");

        // P 核亲和性自愈：每 5 秒检查一次，被系统重置时自动重绑
        if (++_tickCount % AffinityHealEveryTicks == 0 && CpuAffinity.Heal(_server.Current, _cfg.PCoreMask))
            Log?.Invoke("检测到 CPU 亲和性被重置，已重新绑定 P 核。");
    }

    /// <summary>
    /// 安全停机入口：闲置超时且无在途任务时触发。启动后台休眠流程（防重复）：
    /// 10 秒静默观察期（新请求/在途任务即取消）→ 逐槽 save KV 快照 → Kill 整个进程树，杜绝残留。
    /// </summary>
    private void SleepNow()
    {
        lock (_sleepGate)
        {
            if (CurrentPhase != Phase.Running || _sleepPreparing) return;
            _sleepPreparing = true;
        }
        _ = SleepNowCoreAsync();
    }

    private async Task SleepNowCoreAsync()
    {
        try
        {
            var touchAtEntry = Interlocked.Read(ref _lastTouchTicks);
            RaiseStatus($"闲置超时，{SleepGraceSeconds} 秒后休眠（期间保存 KV 缓存）…");
            // 静默观察期：期间任何新请求（Touch 刷新基准点）或在途任务都取消本次休眠
            for (int i = 0; i < SleepGraceSeconds; i++)
            {
                await Task.Delay(1000).ConfigureAwait(false);
                if (Volatile.Read(ref _inflight) > 0 || Interlocked.Read(ref _lastTouchTicks) != touchAtEntry)
                {
                    Log?.Invoke("休眠取消：观察期内有新请求或在途任务。");
                    RaiseStatus("运行中 · 休眠取消（有新活动）");
                    return;
                }
            }
            lock (_sleepGate)
            {
                if (CurrentPhase != Phase.Running) return; // 观察期内被手动停止等：放弃休眠
                SetPhase(Phase.Sleeping);
            }
            await SaveAllSlotsBeforeStopAsync().ConfigureAwait(false);
            Interlocked.Increment(ref _sleepCount); // C-102：休眠计数
            Log?.Invoke($"{IdleMinutes} 分钟无请求，自动休眠（累计 #{Volatile.Read(ref _sleepCount)}，inflight 峰值 {Volatile.Read(ref _inflightPeak)}），正在释放显存…");
            RaiseStatus("闲置超时，正在释放显存…");
            _server.Stop(); // Exited 事件将把状态拉回 Standby
        }
        finally
        {
            lock (_sleepGate) { _sleepPreparing = false; }
        }
    }

    /// <summary>
    /// 休眠前逐槽保存 KV 快照（仅 KvCache=true 的绑定）：进程即将终止，槽位内存 KV 仅此一次落盘机会；
    /// 唤醒后各 key 首次请求将 restore 快照跳过全量 prefill。整体 60s 超时保底（后端卡死不阻塞休眠）。
    /// </summary>
    private async Task SaveAllSlotsBeforeStopAsync()
    {
        var aff = _affinity;
        var kv = _kvCache;
        if (aff == null || kv == null) return; // --slots 未启用：无快照能力，直接休眠
        // O-13：60s CTS——超时后主动取消孤儿 save 任务（原实现 WaitAsync 只停止等待，任务仍在后台运行）
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var saveAll = Task.Run(async () =>
        {
            foreach (var b in aff.Snapshot()) // (Key, App, Slot, LastActive, Preemptive, KvCache)
            {
                if (!b.KvCache)
                {
                    EmitSlot($"休眠前跳过 save：{b.Key}（KV Cache 已关闭）");
                    continue;
                }
                try
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    await kv.SaveAsync(b.Slot, b.Key, cts.Token).ConfigureAwait(false);
                    EmitSlot($"[KV-SAVE] 休眠前快照：{b.Key} → slot{b.Slot}（{sw.Elapsed.TotalSeconds:F1}s）");
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested)
                {
                    return; // O-13：超时取消，放弃剩余槽位
                }
                catch (Exception ex)
                {
                    EmitSlot($"休眠前 KV 保存失败：{b.Key}（{ex.Message}），该槽位 KV 将丢失，唤醒后全量 prefill。");
                }
            }
        }, cts.Token);
        try
        {
            await saveAll.WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Log?.Invoke("休眠前 KV 保存超时（60s），放弃剩余快照，继续休眠。");
            cts.Cancel(); // CTS(60s) 自动取消与 WaitAsync 计时存在竞态：此处确保孤儿任务被取消
        }
    }

    /// <summary>进程退出回调：休眠/运行态退出 → 回到监听待机；唤醒态由唤醒任务自行处理。</summary>
    private void OnServerExited(int code)
    {
        var p = CurrentPhase;
        if (p == Phase.Sleeping || p == Phase.Running)
        {
            bool wasSleep = p == Phase.Sleeping;
            SetPhase(Phase.Standby);
            Log?.Invoke($"llama-server 已退出（退出码 {code}），显存已释放，回到监听待机。");
            RaiseStatus(AutoMode ? "已休眠，继续监听待机。" : "已停止。");
            if (wasSleep) _ = VerifyVramReleasedAsync(); // C-006：休眠后校验显存是否真正回落
        }
    }

    /// <summary>C-006：休眠 Kill 进程树后延迟读显存；未回落到待机水平则告警（衍生子进程孤儿残留）。</summary>
    private async Task VerifyVramReleasedAsync()
    {
        await Task.Delay(3000); // 等 GPU 驱动回收显存稳定
        var mb = await SystemMetrics.GetVramUsedMbAsync();
        if (mb is > VramAlertThresholdMb)
            Log?.Invoke($"警告：休眠后显存占用仍为 {mb} MB（预期接近 0），疑似 llama-server 衍生子进程残留，请在任务管理器中检查。");
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
