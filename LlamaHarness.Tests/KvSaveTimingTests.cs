using LlamaHarness;
using Xunit;

namespace LlamaHarness.Tests;

/// <summary>
/// KV 存档时机修复单测（1.1 首请求存档 / 3.2 Warming eager restore）：
/// - IsAutoPreKey 前缀匹配（大小写不敏感、多前缀、空配置、前缀不完整不误判）
/// - IsAutoSnapshotKey 前缀匹配 + 与自动强占解耦（快照持久化 ≠ 槽位独占）
/// 注：首请求存档块本身依赖真实后端管道（SendAndPipeAsync），属集成路径，此处覆盖可独立判定的前缀匹配逻辑。
/// </summary>
public class KvSaveTimingTests
{
    private static SmartScheduler SchedulerWith(string autoPreApps) =>
        new(new AppConfig { AutoPreemptiveApps = autoPreApps });

    private static SmartScheduler SchedulerWithSnapshot(string snapKeys, string? autoPreApps = null) =>
        new(new AppConfig
        {
            AutoSnapshotKeys = snapKeys,
            AutoPreemptiveApps = autoPreApps ?? "",
        });

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

    [Fact]
    public void IsAutoSnapshotKey_MatchesConfiguredPrefix()
    {
        var s = SchedulerWithSnapshot("trae_,dsh_agent_");
        Assert.True(s.IsAutoSnapshotKey("trae_global"));
        Assert.True(s.IsAutoSnapshotKey("dsh_agent_global"));
    }

    [Fact]
    public void IsAutoSnapshotKey_CaseInsensitive()
    {
        var s = SchedulerWithSnapshot("trae_");
        Assert.True(s.IsAutoSnapshotKey("TRAE_GLOBAL"));
        Assert.True(s.IsAutoSnapshotKey("Trae_Global"));
    }

    [Fact]
    public void IsAutoSnapshotKey_NonMatchingKey_ReturnsFalse()
    {
        var s = SchedulerWithSnapshot("trae_,dsh_agent_");
        Assert.False(s.IsAutoSnapshotKey("webui_foo"));
        // 前缀不完整（缺尾部下划线）：不应误判为 trae_ 前缀
        Assert.False(s.IsAutoSnapshotKey("trae"));
    }

    [Fact]
    public void IsAutoSnapshotKey_EmptyConfig_ReturnsFalse()
    {
        var s = SchedulerWithSnapshot("");
        Assert.False(s.IsAutoSnapshotKey("trae_global"));
    }

    [Fact]
    public void SnapshotAndPreemptive_Decoupled()
    {
        // 快照 key 不在强占列表：有快照持久化，但无槽位冻结
        var snapOnly = SchedulerWithSnapshot("trae_", autoPreApps: "");
        Assert.True(snapOnly.IsAutoSnapshotKey("trae_global"));
        Assert.False(snapOnly.IsAutoPreKey("trae_global"));

        // 强占 key 不在快照列表：有槽位冻结，但无快照持久化
        var preOnly = SchedulerWithSnapshot("", autoPreApps: "trae_");
        Assert.False(preOnly.IsAutoSnapshotKey("trae_global"));
        Assert.True(preOnly.IsAutoPreKey("trae_global"));
    }
}
