namespace LlamaHarness;

/// <summary>
/// bad_alloc 崩溃检测 + 熔断器：
/// - 双源检测：后端 stdout 关键字（"bad allocation"，llama.cpp 任务级 OOM）+ HTTP 错误/流中断（代理侧感知）
/// - WasBadAlloc：stdout 佐证窗口（流中断时判断是否由 bad_alloc 引起）
/// - 熔断器：10 分钟内 ≥3 次确认崩溃 → 停止自动恢复，等待人工介入（加内存/降上下文）
/// </summary>
public static class CrashRecovery
{
    private static readonly object _gate = new();
    /// <summary>stdout 观测到的 bad_alloc 行（时间戳 + 原文），用于流中断佐证。</summary>
    private static readonly Queue<(DateTime Time, string Line)> _observed = new();
    /// <summary>确认的崩溃恢复事件（每次进入恢复流程记一次），用于熔断计数。</summary>
    private static readonly Queue<DateTime> _confirmed = new();

    /// <summary>熔断窗口：10 分钟内最多允许 3 次自动恢复。</summary>
    public const int MaxCrashesInWindow = 3;
    public static TimeSpan Window => TimeSpan.FromMinutes(10);

    /// <summary>后端输出行检测：含 "bad allocation" 关键字即记录观测事件。</summary>
    public static void OnBackendLine(string line)
    {
        if (line.Contains("bad allocation", StringComparison.OrdinalIgnoreCase))
        {
            lock (_gate)
            {
                var now = DateTime.Now;
                _observed.Enqueue((now, line));
                while (_observed.Count > 50) _observed.Dequeue();
            }
        }
    }

    /// <summary>窗口内是否观测到 bad_alloc（流中断的佐证判据）。</summary>
    public static bool WasBadAlloc(TimeSpan window)
    {
        lock (_gate)
        {
            var cutoff = DateTime.Now - window;
            return _observed.Any(e => e.Time >= cutoff);
        }
    }

    /// <summary>记录一次确认的崩溃恢复事件（熔断计数）。</summary>
    public static void RecordCrash()
    {
        lock (_gate)
        {
            var now = DateTime.Now;
            _confirmed.Enqueue(now);
            while (_confirmed.Count > 50) _confirmed.Dequeue();
        }
    }

    /// <summary>熔断器：10 分钟内确认崩溃 < 3 次 → 允许自动恢复；否则熔断（需人工介入）。</summary>
    public static bool AllowRecover()
    {
        lock (_gate)
        {
            var cutoff = DateTime.Now - Window;
            return _confirmed.Count(t => t >= cutoff) < MaxCrashesInWindow;
        }
    }

    /// <summary>当前是否处于熔断状态（UI 红色告警轮询用）。</summary>
    public static bool IsTripped
    {
        get
        {
            lock (_gate)
            {
                var cutoff = DateTime.Now - Window;
                return _confirmed.Count(t => t >= cutoff) >= MaxCrashesInWindow;
            }
        }
    }

    /// <summary>窗口内确认崩溃次数（UI 展示 / 诊断用）。</summary>
    public static int ConfirmedCount
    {
        get
        {
            lock (_gate)
            {
                var cutoff = DateTime.Now - Window;
                return _confirmed.Count(t => t >= cutoff);
            }
        }
    }
}
