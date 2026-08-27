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

    public enum Level { Info, Warn, Error }

    /// <summary>日志级别分类（中英双语）：
    /// 1. 中文关键字（错误/失败/异常 → Error，警告 → Warn）；
    /// 2. llama-server I/W/E 严重度标记；
    /// 3. 英文关键字兜底（error/fatal/critical/exception/failed → Error，warning/warn → Warn）。</summary>
    public static Level Classify(string line)
    {
        if (line.Contains("错误") || line.Contains("失败") || line.Contains("异常")) return Level.Error;
        if (line.Contains("警告")) return Level.Warn;
        var m = SeverityRe.Match(line);
        if (m.Success)
            return m.Groups[1].Value.ToUpper() switch
            {
                "E" => Level.Error,
                "W" => Level.Warn,
                _ => Level.Info,
            };
        if (ErrorKeywordRe.IsMatch(line)) return Level.Error;
        if (WarnKeywordRe.IsMatch(line)) return Level.Warn;
        return Level.Info;
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

    /// <summary>写主日志 harness.log，超限时轮切为 harness.log.1。</summary>
    private static void AppendMain(string stampedLine)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "harness.log");
        Rotate(path, MaxLogBytes);
        File.AppendAllText(path, stampedLine + Environment.NewLine);
    }

    /// <summary>写警告/错误日志 warn_error.log：前 10 条上下文 + 分隔标记 + 本条。</summary>
    private static void AppendWarnError(Level lvl, string stampedLine)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "warn_error.log");
        Rotate(path, MaxWarnBytes);
        var sb = new System.Text.StringBuilder();
        foreach (var l in _recent)
            sb.Append(l).Append(Environment.NewLine);
        sb.Append($"===== {lvl} =====").Append(Environment.NewLine);
        sb.Append(stampedLine).Append(Environment.NewLine);
        File.AppendAllText(path, sb.ToString());
    }

    /// <summary>按大小轮切：path → path.1（覆盖旧备份），随后由调用方新建文件。</summary>
    private static void Rotate(string path, long maxBytes)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length <= maxBytes) return;
        var backup = path + ".1";
        if (File.Exists(backup)) File.Delete(backup);
        File.Move(path, backup);
    }
}
