using System.Text;

namespace LlamaHarness;

/// <summary>
/// UI 日志文件持久化 + 自动轮切 + 警告/错误独立输出：
/// - harness.log：全部日志，超 2MB 自动轮切为 harness.log.1（保留一代备份）
/// - warn_error.log：警告/错误每条独立成块，附带该条之前 10 条日志作上下文，便于排查
/// 线程安全、尽力而为（永不抛出）。
/// </summary>
public static class LogFile
{
    private static readonly object _gate = new();

    /// <summary>日志目录：项目目录下 logs/（写入器首次打开时自动创建）。</summary>
    internal static string LogDir => Path.Combine(AppContext.BaseDirectory, "logs");

    /// <summary>主日志大小上限（字节）。</summary>
    private const long MaxLogBytes = 2_000_000;

    /// <summary>警告/错误日志大小上限（字节）。</summary>
    private const long MaxWarnBytes = 5_000_000;

    /// <summary>警告/错误块附带的前置日志条数。</summary>
    private const int ContextLines = 10;

    /// <summary>最近 N 条带时间戳日志（环形缓冲），供警告/错误块提供上下文。</summary>
    private static readonly Queue<string> _recent = new();

    /// <summary>llama-server 输出严重度标记：时间戳前缀后跟 I/W/E（如 "0.38.265.840 E srv ..."）。</summary>
    private static readonly System.Text.RegularExpressions.Regex SeverityRe =
        new(@"^\d[\d.]*\s+([IWE])\b", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>英文错误关键字（不带 I/W/E 前缀的输出兜底，词边界 + 不区分大小写）。</summary>
    private static readonly System.Text.RegularExpressions.Regex ErrorKeywordRe =
        new(@"\b(error|fatal|critical|exception|failed|failure)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>英文警告关键字。</summary>
    private static readonly System.Text.RegularExpressions.Regex WarnKeywordRe =
        new(@"\b(warning|warn)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>llama.cpp 已知良性噪声（3.3 日志标准化）：剪枝/合并模型残留的 unused tensor 警告——不进告警流，仅写主日志。</summary>
    private static readonly System.Text.RegularExpressions.Regex UnusedTensorRe =
        new(@"model has unused tensor blk\.\d+", System.Text.RegularExpressions.RegexOptions.Compiled);

    public enum Level { Info, Warn, Error }

    /// <summary>日志级别分类（中英双语）：
    /// 0. 已知良性噪声（unused tensor）→ Info（不写 warn_error.log）；
    /// 1. 中文关键字（错误/失败/异常 → Error，警告 → Warn）；
    /// 2. llama-server I/W/E 严重度标记；
    /// 3. 英文关键字兜底（error/fatal/critical/exception/failed → Error，warning/warn → Warn）。</summary>
    public static Level Classify(string line)
    {
        if (UnusedTensorRe.IsMatch(line)) return Level.Info; // 3.3：良性警告降级，防误告警
        if (line.Contains("错误") || line.Contains("失败") || line.Contains("异常")) return Level.Error;
        if (line.Contains("警告")) return Level.Warn;
        var m = SeverityRe.Match(line);
        if (m.Success)
            return m.Groups[1].Value switch // 正则字符类 [IWE] 只匹配大写，无需 ToUpper
            {
                "E" => Level.Error,
                "W" => Level.Warn,
                _ => Level.Info,
            };
        if (ErrorKeywordRe.IsMatch(line)) return Level.Error;
        if (WarnKeywordRe.IsMatch(line)) return Level.Warn;
        return Level.Info;
    }

    /// <summary>最近日志快照（/__status__ 的 recent_logs 数据源；锁内复制，含全部 harness 侧日志）。</summary>
    public static string[] SnapshotRecent()
    {
        lock (_gate)
        {
            return _recent.ToArray();
        }
    }

    /// <summary>追加一行日志（可来自任意线程）：写主日志 + 按级别写警告/错误日志。</summary>
    public static void Append(string line)
    {
        lock (_gate)
        {
            try
            {
                var stamped = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {line}";
                AppendMain(stamped);
                var lvl = Classify(line);
                if (lvl != Level.Info)
                    AppendWarnError(lvl, stamped);
                // 维护上下文环形缓冲（当前行未入队前，_recent 即"该条之前 10 条"）
                _recent.Enqueue(stamped);
                while (_recent.Count > ContextLines) _recent.Dequeue();
            }
            catch
            {
                // 尽力而为：磁盘满/权限等问题不影响主流程
            }
        }
    }

    // ==================== E-6：常驻 StreamWriter 批量写 ====================

    /// <summary>三个日志文件的常驻写入器（懒打开，缓冲写）。</summary>
    private static readonly LogStreamWriter _mainWriter = new(Path.Combine(AppContext.BaseDirectory, "logs", "harness.log"));
    private static readonly LogStreamWriter _warnWriter = new(Path.Combine(AppContext.BaseDirectory, "logs", "warn_error.log"));
    private static readonly LogStreamWriter _slotWriter = new(Path.Combine(AppContext.BaseDirectory, "logs", "slot.log"));

    /// <summary>150ms 周期 Flush 定时器（与 UI 防抖同节奏）：批量落盘，替代旧实现每行 open/write/close。</summary>
    private static readonly System.Threading.Timer _flushTimer = new(OnFlushTick, null, 150, 150);

    private static void OnFlushTick(object? _)
    {
        lock (_gate)
        {
            _mainWriter.Flush();
            _warnWriter.Flush();
            _slotWriter.Flush();
        }
    }

    /// <summary>进程退出时调用：Flush + 关闭全部写入器（防缓冲丢失/句柄泄漏）。</summary>
    public static void Shutdown()
    {
        lock (_gate)
        {
            _flushTimer.Dispose();
            _mainWriter.Dispose();
            _warnWriter.Dispose();
            _slotWriter.Dispose();
        }
    }

    /// <summary>追加一行槽位日志（可来自任意线程）：独立写入 logs/slot.log，超 2MB 轮切。用于绑定/驱逐/KV Cache 事件追溯。</summary>
    public static void SlotAppend(string line)
    {
        lock (_gate)
        {
            try
            {
                var stamped = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {line}";
                if (_slotWriter.RotateIfNeeded(MaxLogBytes)) { /* 已轮切，下条自动重开 */ }
                _slotWriter.Write(stamped + Environment.NewLine);
            }
            catch
            {
                // 尽力而为：磁盘满/权限等问题不影响主流程
            }
        }
    }

    /// <summary>写主日志 logs/harness.log，超限时轮切为 harness.log.1。（调用方已持 _gate）</summary>
    private static void AppendMain(string stampedLine)
    {
        if (_mainWriter.RotateIfNeeded(MaxLogBytes)) { /* 已轮切 */ }
        _mainWriter.Write(stampedLine + Environment.NewLine);
    }

    /// <summary>写警告/错误日志 logs/warn_error.log：前 10 条上下文 + 分隔标记 + 本条。（调用方已持 _gate）</summary>
    private static void AppendWarnError(Level lvl, string stampedLine)
    {
        if (_warnWriter.RotateIfNeeded(MaxWarnBytes)) { /* 已轮切 */ }
        var sb = new System.Text.StringBuilder();
        foreach (var l in _recent)
            sb.Append(l).Append(Environment.NewLine);
        sb.Append($"===== {lvl} =====").Append(Environment.NewLine);
        sb.Append(stampedLine).Append(Environment.NewLine);
        _warnWriter.Write(sb.ToString());
    }
}
