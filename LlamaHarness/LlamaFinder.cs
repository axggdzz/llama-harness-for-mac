using System.Text;

namespace LlamaHarness;

/// <summary>
/// llama-server.exe 定位（优先级：手动指定 → PATH → 常见安装位置）
/// 以及启动命令行拼接。纯逻辑，无 UI 依赖。
/// </summary>
public static class LlamaFinder
{
    /// <summary>按优先级查找 llama-server.exe，找不到返回 null。</summary>
    public static string? Find(string configuredPath)
    {
        // 1. 配置中手动指定的路径
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            try
            {
                if (File.Exists(configuredPath.Trim()))
                    return Path.GetFullPath(configuredPath.Trim());
            }
            catch
            {
                // 非法路径字符串，忽略继续搜索
            }
        }

        // 2. PATH 环境变量
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), "llama-server.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch
            {
                // PATH 中含非法目录时跳过
            }
        }

        // 3. 常见安装位置
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "llama-server.exe"),
            @"C:\llama.cpp\build\bin\Release\llama-server.exe",
            Path.Combine(userProfile, "llama.cpp", "build", "bin", "Release", "llama-server.exe"),
        };
        foreach (var c in candidates)
        {
            try
            {
                if (File.Exists(c)) return c;
            }
            catch
            {
                // 忽略非法路径
            }
        }
        return null;
    }

    /// <summary>
    /// 拼接 llama-server 完整命令行参数。
    /// 模板：-m &lt;model&gt; --port &lt;p&gt; -c &lt;c&gt; -ngl &lt;n&gt; --parallel &lt;np&gt; [--no-kv-unified] -t &lt;t&gt; [附加参数]
    /// portOverride 用于智能模式下后端端口（前端端口 + 1）；
    /// threadsOverride 用于 P 核掩码生效时钳制线程数（防超订）。
    /// 附加参数原样拼入（不做再解析），含空格的值需用户自行加引号，见 AppConfig.ExtraArgs。
    /// </summary>
    public static string BuildArgs(AppConfig cfg, int? portOverride = null, int? threadsOverride = null)
    {
        var sb = new StringBuilder();
        sb.Append($"-m \"{cfg.ModelPath}\"");
        sb.Append($" --port {(portOverride ?? cfg.Port)}");
        sb.Append($" -c {cfg.CtxSize}");
        sb.Append($" -ngl {cfg.Ngl}");
        sb.Append($" --parallel {cfg.Parallel}");
        if (cfg.NoKvUnified)
            sb.Append(" --no-kv-unified");
        int threads = threadsOverride ?? cfg.Threads;
        if (threads > 0)
            sb.Append($" -t {threads}");
        if (!string.IsNullOrWhiteSpace(cfg.ExtraArgs))
            sb.Append(' ').Append(cfg.ExtraArgs.Trim());
        return sb.ToString();
    }
}
