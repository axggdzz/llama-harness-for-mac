using System.Collections.Specialized;
using LlamaHarness;
using Xunit;

namespace LlamaHarness.Tests;

/// <summary>
/// SlotAffinity E-5 并发单测：排队等待（全槽强占）不再阻塞其他请求的槽位操作。
/// 旧实现 Sleep-in-lock：一个请求排队 → GetSlot/SetPreemptive/Snapshot 全部被卡最长 30s。
/// </summary>
public class SlotAffinityConcurrencyTests
{
    private static string BindingsPath => Path.Combine(AppContext.BaseDirectory, "config", "slot_bindings.json");

    /// <summary>测试隔离：清除共享持久化文件，避免跨用例串扰。</summary>
    private static void Cleanup()
    {
        try { if (File.Exists(BindingsPath)) File.Delete(BindingsPath); } catch { /* 忽略 */ }
    }

    private static NameValueCollection Headers(string userId) => new() { { "x-deepseek-harness-user-id", userId } };

    [Fact]
    public void WaitQueue_DoesNotBlockOtherSlotOperations_AndAcquiresAfterRelease()
    {
        Cleanup();
        var aff = new SlotAffinity(2, maxWaitSeconds: 3);

        // 占满两槽并强占
        var a = aff.GetSlot(Headers("uA"));
        var b = aff.GetSlot(Headers("uB"));
        Assert.Equal(0, a.Slot);
        Assert.Equal(1, b.Slot);
        aff.SetPreemptive(a.Key!, true);
        aff.SetPreemptive(b.Key!, true);

        // C 进入排队（全槽强占）
        var cTask = Task.Run(() => aff.GetSlot(Headers("uC")));
        Thread.Sleep(200); // 让 C 进入等待循环

        // C 等待期间：其他槽位操作必须不被阻塞（旧实现被 Sleep-in-lock 卡住 ≥1s/轮）
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _ = aff.Snapshot();
        bool aStillPreemptive = aff.IsPreemptive(a.Key!);
        aff.SetPreemptive(b.Key!, false); // 释放 B
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 500, $"槽位操作耗时 {sw.ElapsedMilliseconds}ms，被排队阻塞");
        Assert.True(aStillPreemptive);

        // C 应在 ≤4s 内拿到 B 的槽位（驱逐非强占 B）
        Assert.True(cTask.Wait(TimeSpan.FromSeconds(4)), "C 未在 4s 内获得槽位");
        var c = cTask.Result;
        Assert.Equal(b.Slot, c.Slot);
        Assert.Equal("dsh_rule_uC", c.Key);
    }

    [Fact]
    public void WaitQueue_TimeoutDegradesToRandomSlotWithoutBinding()
    {
        Cleanup();
        var aff = new SlotAffinity(2, maxWaitSeconds: 1);
        var a = aff.GetSlot(Headers("vA"));
        var b = aff.GetSlot(Headers("vB"));
        aff.SetPreemptive(a.Key!, true);
        aff.SetPreemptive(b.Key!, true);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var c = aff.GetSlot(Headers("vC"));
        sw.Stop();

        Assert.Null(c.Key); // 超时降级：随机槽，不建绑定
        Assert.True(sw.ElapsedMilliseconds >= 900, $"超时路径仅耗时 {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public void ExistingBinding_RefreshesAndReturnsSameSlot()
    {
        Cleanup();
        var aff = new SlotAffinity(2);
        var first = aff.GetSlot(Headers("wA"));
        var second = aff.GetSlot(Headers("wA"));
        Assert.Equal(first.Slot, second.Slot);
        Assert.False(second.NewBinding);
    }
}
