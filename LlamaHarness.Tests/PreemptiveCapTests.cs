using System.Collections.Specialized;
using LlamaHarness;
using Xunit;

namespace LlamaHarness.Tests;

/// <summary>
/// 强占上限单测（保"至少 1 槽给非强占新任务"不变量）：
/// - 启动时 EnforcePreemptiveCap 裁剪超额强占
/// - 请求时 TryAllocateLocked 驱逐最早活跃/Tool 锁定
/// - Tool 链锁定优先牺牲
/// </summary>
public class PreemptiveCapTests
{
    private static string BindingsPath => Path.Combine(AppContext.BaseDirectory, "config", "slot_bindings.json");

    private static void Cleanup()
    {
        try { if (File.Exists(BindingsPath)) File.Delete(BindingsPath); } catch { /* 忽略 */ }
    }

    private static NameValueCollection Headers(string userId) => new() { { "x-deepseek-harness-user-id", userId } };

    [Fact]
    public void SingleSlot_TwoPreemptive_CapLeavesOneFree()
    {
        Cleanup();
        var aff = new SlotAffinity(1); // 单槽，cap = 0（强占数 ≤ 0）

        // A 占槽并强占
        var a = aff.GetSlot(Headers("uA"));
        aff.SetPreemptive(a.Key!, true);

        // B 请求：全槽强占 + B 也是强占 → 驱逐 A（最早活跃），B 拿到槽
        var b = aff.GetSlot(Headers("uB"), autoPreemptive: new[] { "dsh_rule_" });
        Assert.Equal(a.Slot, b.Slot); // B 拿到 A 的槽
        Assert.Equal("dsh_rule_uB", b.Key);

        // A 再请求：B 是强占，A 也是强占 → 驱逐 B（最早活跃），A 拿回槽
        var a2 = aff.GetSlot(Headers("uA"), autoPreemptive: new[] { "dsh_rule_" });
        Assert.Equal(b.Slot, a2.Slot);
        Assert.Equal("dsh_rule_uA", a2.Key);

        // 非强占新 key C：全槽强占 → 排队超时降级随机槽（不建绑定）
        var c = aff.GetSlot(Headers("uC"));
        Assert.Null(c.Key); // 超时降级
    }

    [Fact]
    public void MultiSlot_ThreePreemptive_CapLeavesOneFree()
    {
        Cleanup();
        var aff = new SlotAffinity(3); // 3 槽，cap = 2（强占数 ≤ 2）

        // A/B/C 各占一槽并强占
        var a = aff.GetSlot(Headers("uA"));
        var b = aff.GetSlot(Headers("uB"));
        var c = aff.GetSlot(Headers("uC"));
        aff.SetPreemptive(a.Key!, true);
        aff.SetPreemptive(b.Key!, true);
        aff.SetPreemptive(c.Key!, true);

        // 启动时强制：裁剪到 cap=2，驱逐最早活跃的 A
        var evicted = aff.EnforcePreemptiveCap();
        Assert.Equal(new[] { "dsh_rule_uA" }, evicted);
        Assert.False(aff.IsPreemptive("dsh_rule_uA"));
        Assert.True(aff.IsPreemptive("dsh_rule_uB"));
        Assert.True(aff.IsPreemptive("dsh_rule_uC"));

        // D（非强占）现在能拿到空闲槽（A 的槽已释放）
        var d = aff.GetSlot(Headers("uD"));
        Assert.NotNull(d.Key);
        Assert.Equal(a.Slot, d.Slot); // D 拿到 A 释放的槽
    }

    [Fact]
    public void ToolLocked_PriorityEviction()
    {
        Cleanup();
        var aff = new SlotAffinity(2); // 2 槽，cap = 1

        // A 占槽 + 手动强占
        var a = aff.GetSlot(Headers("uA"));
        aff.SetPreemptive(a.Key!, true);

        // B 占槽 + Tool 链锁定（瞬态强占）
        var b = aff.GetSlot(Headers("uB"));
        aff.MarkToolLocked(b.Key!);
        aff.SetPreemptive(b.Key!, true);

        // C（强占）请求：全槽强占 → 驱逐 Tool 锁定的 B（优先于手动强占的 A）
        var c = aff.GetSlot(Headers("uC"), autoPreemptive: new[] { "dsh_rule_" });
        Assert.Equal(b.Slot, c.Slot); // C 拿到 B 的槽
        Assert.Equal("dsh_rule_uC", c.Key);

        // A 仍是强占（未被牺牲）
        Assert.True(aff.IsPreemptive(a.Key!));
    }

    [Fact]
    public void EnforcePreemptiveCap_NoOpWhenUnderCap()
    {
        Cleanup();
        var aff = new SlotAffinity(3); // cap = 2

        // 只有 1 个强占 → 不裁剪
        var a = aff.GetSlot(Headers("uA"));
        aff.SetPreemptive(a.Key!, true);

        var evicted = aff.EnforcePreemptiveCap();
        Assert.Empty(evicted);
        Assert.True(aff.IsPreemptive(a.Key!));
    }
}
