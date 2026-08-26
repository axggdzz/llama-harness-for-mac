using System.Text.Json;

namespace LlamaLauncher;

/// <summary>
/// 应用配置模型。默认值为实测黄金底参：
/// ctx=262144 / ngl=999 / parallel=1 / no-kv-unified 开启。
/// 持久化为程序目录下的 config.json。
/// </summary>
public class AppConfig
{
    public string ExePath { get; set; } = "";
    public string ModelPath { get; set; } = "";
    public int Port { get; set; } = 8080;
    public int CtxSize { get; set; } = 262144;   // -c 上下文长度（黄金底参）
    public int Ngl { get; set; } = 999;          // -ngl GPU 层数（黄金底参）
    public int Parallel { get; set; } = 1;       // --parallel 并发序列（黄金底参）
    public bool NoKvUnified { get; set; } = true;// --no-kv-unified（黄金底参）
    public int Threads { get; set; } = Environment.ProcessorCount; // -t 线程数
    public string ExtraArgs { get; set; } = "";
    public bool AutoMode { get; set; } = true;       // 智能按需模式：代理监听 8080 + 按需唤醒 + 闲置休眠
    public int IdleMinutes { get; set; } = 15;       // 无请求自动休眠分钟数
    // P 核亲和性掩码（十六进制）：13900F 本机 P 核 = 逻辑 CPU 0–15；留空 = 禁用绑定
    public string PCoreMask { get; set; } = "0x0000FFFF";

    private static string ConfigPath => Path.Combine(AppContext.BaseDirectory, "config.json");
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    /// <summary>加载配置；文件不存在返回默认值，损坏则回退默认值并通过 out 报告错误。</summary>
    public static AppConfig Load(out string? loadError)
    {
        loadError = null;
        try
        {
            if (!File.Exists(ConfigPath))
                return new AppConfig();

            var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath), JsonOpts);
            if (cfg == null)
                throw new InvalidOperationException("反序列化结果为空");

            // 数值兜底：越界时回退黄金默认值
            if (cfg.Port is < 1 or > 65535) cfg.Port = 8080;
            if (cfg.CtxSize <= 0) cfg.CtxSize = 262144;
            if (cfg.Ngl < 0) cfg.Ngl = 999;
            if (cfg.Parallel <= 0) cfg.Parallel = 1;
            if (cfg.Threads <= 0) cfg.Threads = Environment.ProcessorCount;
            if (cfg.IdleMinutes <= 0) cfg.IdleMinutes = 15;
            return cfg;
        }
        catch (Exception ex)
        {
            loadError = $"config.json 读取失败，已回退默认值：{ex.Message}";
            return new AppConfig();
        }
    }

    /// <summary>保存配置到程序目录，返回是否成功。</summary>
    public bool Save()
    {
        try
        {
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, JsonOpts));
            return true;
        }
        catch
        {
            return false;
        }
    }
}
