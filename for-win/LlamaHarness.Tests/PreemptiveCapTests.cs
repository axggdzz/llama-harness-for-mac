using System.Collections.Specialized;
using LlamaHarness;
using Xunit;

namespace LlamaHarness.Tests;

/// <summary>
/// 强占上限单测（保"至少 1 槽给非强占新任务"不变量）：
/// - 启动时 EnforcePreemptiveCap 裁剪超额强占
/// - 请求时 TryAllocateLocked 驱逐最早活跃/Tool 锁定
/// - 已有绑定刷新时检查 cap（防"启动裁剪→下次请求又变回强占"死循环）
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
    public void SingleSlot_AutoPreemptive_CapZero_NoDeadlock()
    {
        Cleanup();
        var aff = new SlotAffinity(1); // 单槽，cap = 0（强占数 ≤ 0）

        // A（自动强占应用）请求：cap=0 → 新建绑定时 finalPre=false，不设强占
        var a = aff.GetSlot(Headers("uA"), autoPreemptive: new[] { "dsh_rule_" });
        Assert.Equal(0, a.Slot);
        Assert.False(aff.IsPreemptive(a.Key!)); // cap=0：不会变强占

        // A 再次请求：已有绑定刷新，autoPre=true 但 cap=0 → 不设强占（防死循环）
        var a2 = aff.GetSlot(Headers("uA"), autoPreemptive: new[] { "dsh_rule_" });
        Assert.Equal(a.Slot, a2.Slot);
        Assert.False(aff.IsPreemptive(a2.Key!));

        // B（非强占）请求：A 非强占 → LRU 驱逐 A → B 拿到槽（不死锁！）
        var b = aff.GetSlot(Headers("uB"));
        Assert.Equal(a.Slot, b.Slot);
        Assert.NotNull(b.Key);

        // C（非强占）请求：B 非强占 → LRU 驱逐 B → C 拿到槽
        var c = aff.GetSlot(Headers("uC"));
        Assert.Equal(b.Slot, c.Slot);
        Assert.NotNull(c.Key);
    }

    [Fact]
    public void SingleSlot_ManualPreemptive_ThenAutoPre_RestoresCap()
    {
        Cleanup();
        var aff = new SlotAffinity(1); // 单槽，cap = 0

        // A 占槽 + 手动强占（绕过 cap 检查，直接设）
        var a = aff.GetSlot(Headers("uA"));
        aff.SetPreemptive(a.Key!, true);
        Assert.True(aff.IsPreemptive(a.Key!));

        // 启动时强制裁剪：cap=0 → A 取消强占
        var evicted = aff.EnforcePreemptiveCap();
        Assert.Contains("dsh_rule_uA", evicted);
        Assert.False(aff.IsPreemptive(a.Key!));

        // A 再请求（autoPre）：已有绑定刷新，cap=0 → 不设强占（防死循环）
        var a2 = aff.GetSlot(Headers("uA"), autoPreemptive: new[] { "dsh_rule_" });
        Assert.False(aff.IsPreemptive(a2.Key!));

        // B 请求：A 非强占 → LRU 驱逐 → B 拿到槽（不死锁）
        var b = aff.GetSlot(Headers("uB"));
        Assert.NotNull(b.Key);
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

        // D（非强占）现在能拿到空闲槽（A 的槽已释放强占，可被 LRU 驱逐）
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
