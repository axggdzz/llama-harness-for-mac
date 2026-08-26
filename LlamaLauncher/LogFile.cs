namespace LlamaLauncher;

/// <summary>
/// UI 日志文件持久化：AppendLog 的每行同时追加到 exe 旁 launcher.log，
/// 进程退出后仍可回溯唤醒/休眠/推理全过程。线程安全、尽力而为（永不抛出）。
/// </summary>
public static class LogFile
{
    private static readonly object _gate = new();

    /// <summary>日志文件大小上限（字节）：超出时仅保留最近一半。</summary>
    private const long MaxLogBytes = 2_000_000;

    /// <summary>追加一行日志（带完整日期时间戳；可来自任意线程）。</summary>
    public static void Append(string line)
    {
        lock (_gate)
        {
            try
            {
                var path = Path.Combine(AppContext.BaseDirectory, "launcher.log");
                var info = new FileInfo(path);
                if (info.Exists && info.Length > MaxLogBytes)
                {
                    // 超限：保留最近一半，丢弃更早内容
                    var all = File.ReadAllText(path);
                    File.WriteAllText(path, all.Substring(all.Length / 2));
                }
                File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {line}{Environment.NewLine}");
            }
            catch
            {
                // 尽力而为：磁盘满/权限等问题不影响主流程
            }
        }
    }
}
