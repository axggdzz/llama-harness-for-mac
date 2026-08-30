using LlamaHarness;
using Xunit;

namespace LlamaHarness.Tests;

/// <summary>
/// KV 存档时机修复单测（1.1 首请求存档）：
/// - IsAutoPreKey 前缀匹配（大小写不敏感、多前缀、空配置、前缀不完整不误判）
/// 注：首请求存档块本身依赖真实后端管道（SendAndPipeAsync），属集成路径，此处覆盖可独立判定的前缀匹配逻辑。
/// </summary>
public class KvSaveTimingTests
{
    private static SmartScheduler SchedulerWith(string autoPreApps) =>
        new(new AppConfig { AutoPreemptiveApps = autoPreApps });

    [Fact]
    public void IsAutoPreKey_MatchesConfiguredPrefix()
    {
        var s = SchedulerWith("trae_,dsh_agent_");
        Assert.True(s.IsAutoPreKey("trae_global"));
        Assert.True(s.IsAutoPreKey("dsh_agent_global"));
    }

    [Fact]
    public void IsAutoPreKey_CaseInsensitive()
    {
        var s = SchedulerWith("trae_");
        Assert.True(s.IsAutoPreKey("TRAE_GLOBAL"));
        Assert.True(s.IsAutoPreKey("Trae_Global"));
    }

    [Fact]
    public void IsAutoPreKey_NonMatchingKey_ReturnsFalse()
    {
        var s = SchedulerWith("trae_,dsh_agent_");
        Assert.False(s.IsAutoPreKey("webui_foo"));
        // 前缀不完整（缺尾部下划线）：不应误判为 trae_ 前缀
        Assert.False(s.IsAutoPreKey("trae"));
    }

    [Fact]
    public void IsAutoPreKey_EmptyConfig_ReturnsFalse()
    {
        var s = SchedulerWith("");
        Assert.False(s.IsAutoPreKey("trae_global"));
    }
}
