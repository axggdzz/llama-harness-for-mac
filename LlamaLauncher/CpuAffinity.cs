using System.Diagnostics;
using System.Globalization;

namespace LlamaLauncher;

/// <summary>
/// llama-server P 核亲和性绑定（Intel 混合架构 CPU，如 13900F）。
/// llama.cpp 线程池锁步推进，混跑 E 核会被最慢核心拖速，故绑定纯 P 核。
/// 掩码来自配置（十六进制，如 0x0000FFFF = 逻辑 CPU 0–15）；留空 = 禁用绑定。
/// 说明：本机虚拟化环境下 GetLogicalProcessorInformation 返回数据不可信，
/// 故不做动态拓扑检测，由用户按 CPU-Z 确认的布局填写掩码。
/// </summary>
public static class CpuAffinity
{
    /// <summary>解析十六进制掩码字符串；无效或留空返回 null（= 禁用）。</summary>
    public static long? ParseMask(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        string t = s.Trim();
        // .NET 的 HexNumber 实测不接受 "0x"/"&h" 前缀，需手动剥离
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || t.StartsWith("&h", StringComparison.OrdinalIgnoreCase))
            t = t.Substring(2);
        if (long.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long v) && v > 0)
            return v;
        return null;
    }

    /// <summary>把进程绑定到指定 P 核掩码。成功返回掩码文本，否则 null。</summary>
    public static string? Apply(Process? p, string? maskText)
    {
        var mask = ParseMask(maskText);
        if (mask == null || p is null) return null;
        try
        {
            if (p.HasExited) return null;
            p.ProcessorAffinity = new IntPtr(mask.Value);
            return $"0x{mask.Value:X8}";
        }
        catch
        {
            return null; // 进程正在退出等瞬时状态
        }
    }

    /// <summary>自愈：亲和性被系统重置时重新绑定。返回是否发生了重绑。</summary>
    public static bool Heal(Process? p, string? maskText)
    {
        var mask = ParseMask(maskText);
        if (mask == null || p is null) return false;
        try
        {
            if (p.HasExited) return false;
            long cur = p.ProcessorAffinity.ToInt64();
            if (cur != mask.Value)
            {
                p.ProcessorAffinity = new IntPtr(mask.Value);
                return true;
            }
        }
        catch
        {
            // 进程正在退出等瞬时状态，忽略
        }
        return false;
    }
}
