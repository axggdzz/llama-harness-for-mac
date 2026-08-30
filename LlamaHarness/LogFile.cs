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

    /// <summary>日志目录：项目目录下 logs/（首次写入时自动创建）。</summary>
    internal static string LogDir => Path.Combine(AppContext.BaseDirectory, "logs");

    /// <summary>确保日志目录存在（幂等）。</summary>
    private static void EnsureLogDir()
    {
        var dir = LogDir;
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
    }

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
                _slotWriter.WriteLine(stamped + Environment.NewLine);
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
        _mainWriter.WriteLine(stampedLine + Environment.NewLine);
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
        _warnWriter.WriteBlock(sb.ToString());
    }

    /// <summary>常驻日志写入器（E-6）：单个 StreamWriter 缓冲写，150ms 定时 Flush，按大小轮切（close→rename→reopen）。
    /// 替代旧实现每行 File.AppendAllText（open/write/close 系统调用），推理期 I/O 降一个数量级。</summary>
    private sealed class LogStreamWriter : IDisposable
    {
        private readonly string _path;
        private StreamWriter? _writer;
        private long _bytes;
        private bool _initialized;

        public LogStreamWriter(string path) => _path = path;

        /// <summary>写一行（调用方持 LogFile._gate）。</summary>
        public void WriteLine(string line)
        {
            EnsureOpen();
            _writer!.Write(line);
            _bytes += Encoding.UTF8.GetByteCount(line);
        }

        /// <summary>写多行块（警告/错误上下文块）。</summary>
        public void WriteBlock(string block)
        {
            EnsureOpen();
            _writer!.Write(block);
            _bytes += Encoding.UTF8.GetByteCount(block);
        }

        public void Flush()
        {
            try { _writer?.Flush(); } catch { /* 尽力而为 */ }
        }

        /// <summary>按大小轮切：close → path→path.1（覆盖旧备份）→ 下次写自动重开。返回是否发生轮切。</summary>
        public bool RotateIfNeeded(long maxBytes)
        {
            if (_bytes <= maxBytes) return false;
            CloseQuiet();
            try
            {
                var backup = _path + ".1";
                if (File.Exists(backup)) File.Delete(backup);
                File.Move(_path, backup);
            }
            catch
            {
                // 轮切失败不影响写入（下次 EnsureOpen 仍会打开原文件追加）
            }
            _bytes = 0;
            return true;
        }

        private void EnsureOpen()
        {
            if (_writer != null) return;
            EnsureLogDir();
            _writer = new StreamWriter(new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read), Encoding.UTF8, 4096);
            if (!_initialized)
            {
                // 首次打开：以既有文件大小为轮切基准（追加模式不重置计数）
                var fi = new FileInfo(_path);
                _bytes = fi.Exists ? fi.Length : 0;
                _initialized = true;
            }
        }

        private void CloseQuiet()
        {
            try { _writer?.Dispose(); } catch { /* 尽力而为 */ }
            _writer = null;
        }

        public void Dispose()
        {
            Flush();
            CloseQuiet();
        }
    }
}
