using System.Diagnostics;

namespace LlamaLauncher;

/// <summary>
/// llama-server 进程封装：后台静默运行（无黑框），逐行输出事件，退出码事件。
/// </summary>
public sealed class LlamaServerProcess : IDisposable
{
    private Process? _proc;

    /// <summary>当前是否还有存活的进程。</summary>
    public bool IsRunning => _proc is { HasExited: false };

    /// <summary>输出一行日志（stdout/stderr），可能来自非 UI 线程。</summary>
    public event Action<string>? OutputLine;

    /// <summary>进程退出，参数为退出码，可能来自非 UI 线程。</summary>
    public event EventHandler<int>? Exited;

    /// <summary>启动 llama-server。要求当前无存活进程。</summary>
    public void Start(string exePath, string args, string workingDir)
    {
        if (IsRunning)
            throw new InvalidOperationException("已有进程在运行。");

        // 清理上一次的 Process 对象
        _proc?.Dispose();

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = args,
            UseShellExecute = false,       // 必须：允许重定向输出
            CreateNoWindow = true,         // 后台静默，杜绝黑框弹窗
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workingDir,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };

        _proc = new Process
        {
            StartInfo = psi,
            EnableRaisingEvents = true,
        };
        _proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null) OutputLine?.Invoke(e.Data);
        };
        _proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null) OutputLine?.Invoke(e.Data);
        };
        _proc.Exited += (_, _) =>
        {
            int code = 0;
            try { code = _proc.ExitCode; } catch { /* 极端情况下取不到 */ }
            Exited?.Invoke(this, code);
        };

        _proc.Start();
        _proc.BeginOutputReadLine();
        _proc.BeginErrorReadLine();
    }

    /// <summary>停止：终止整个进程树（含派生子进程）。已退出则忽略。</summary>
    public void Stop()
    {
        var p = _proc;
        if (p is null || p.HasExited) return;
        try
        {
            p.Kill(entireProcessTree: true);
        }
        catch
        {
            // 进程可能刚好自行退出，忽略
        }
    }

    public void Dispose()
    {
        try { _proc?.Dispose(); } catch { }
    }
}
