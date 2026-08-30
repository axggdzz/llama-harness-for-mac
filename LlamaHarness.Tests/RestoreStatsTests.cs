using LlamaHarness;
using Xunit;

namespace LlamaHarness.Tests;

/// <summary>
/// Restore 命中率可观测单测（3.1）：
/// - 四象限判定（HitByDelta / FullPrefill / MidRange 保守 miss / savedN 未知退化）
/// - prompt eval 行解析
/// - FIFO 归属（最旧优先、空队列返回 null、TTL 防错位）
/// - 四象限计数（false_miss / false_hit）
/// - 告警状态迁移（&lt;50% 红、同级别不重复）
/// - 持久化往返（原子写 + Load 恢复）
/// </summary>
public class RestoreStatsTests
{
    /// <summary>测试用临时持久化路径（避免污染真实 config/）。</summary>
    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"restore_stats_test_{Guid.NewGuid():N}.json");

    [Fact]
    public void Judge_HitByDelta_SmallEval()
    {
        var (hit, reason) = RestoreStats.Judge(656, 171200);
        Assert.True(hit);
        Assert.Equal("HitByDelta", reason);
    }

    [Fact]
    public void Judge_FullPrefill_LargeEval()
    {
        var (hit, reason) = RestoreStats.Judge(171856, 171200);
        Assert.False(hit);
        Assert.Equal("FullPrefill", reason);
    }

    [Fact]
    public void Judge_MidRange_ConservativeMiss()
    {
        // 50000 > 4096（非命中）且 < 171200*0.5=85600（非全量）→ 中间态保守 miss
        var (hit, reason) = RestoreStats.Judge(50000, 171200);
        Assert.False(hit);
        Assert.Equal("MidRange", reason);
    }

    [Fact]
    public void Judge_SavedN_Zero_DegratesToMiss()
    {
        // savedN 未知：全量估计退化为 eval 值本身 → 恒为 FullPrefill miss
        var (hit, reason) = RestoreStats.Judge(100000, 0);
        Assert.False(hit);
        Assert.Equal("FullPrefill", reason);
    }

    [Fact]
    public void TryParsePromptEvalTokens_ValidLine()
    {
        Assert.True(RestoreStats.TryParsePromptEvalTokens(
            "srv  prompt eval time = 123.4 ms / 656 tokens ( 5.324 ms/token)", out int n));
        Assert.Equal(656, n);
    }

    [Fact]
    public void TryParsePromptEvalTokens_NonMatchingLine()
    {
        Assert.False(RestoreStats.TryParsePromptEvalTokens("eval time = 10 ms / 5 tokens", out _));
        Assert.False(RestoreStats.TryParsePromptEvalTokens("total time = 1234 ms", out _));
    }

    [Fact]
    public void Fifo_PopsOldest_First()
    {
        var s = new RestoreStats(TempPath());
        s.RecordRequest("key_a", 0, false, 171200);
        s.RecordRequest("key_b", 1, true, 171200);
        var r1 = s.OnPromptEval(100);   // 弹 key_a
        var r2 = s.OnPromptEval(100);   // 弹 key_b
        Assert.NotNull(r1);
        Assert.NotNull(r2);
        Assert.Equal("key_a", r1!.Key);
        Assert.Equal("key_b", r2!.Key);
    }

    [Fact]
    public void Fifo_Empty_ReturnsNull()
    {
        var s = new RestoreStats(TempPath());
        Assert.Null(s.OnPromptEval(100));
    }

    [Fact]
    public void Fifo_Ttl_ExpiredEntry_Dropped()
    {
        var s = new RestoreStats(TempPath()) { PendingTtl = TimeSpan.FromMilliseconds(1) };
        s.RecordRequest("key_x", 0, false, 171200);
        Thread.Sleep(30); // 条目过期（TTL 防错位：非判定上下文任务的 print_timing 不应消费旧条目）
        Assert.Null(s.OnPromptEval(100));
    }

    [Fact]
    public void FourQuadrant_CountsFalseMissAndFalseHit()
    {
        var s = new RestoreStats(TempPath());
        // wrapper 报 MISS + 实际命中 → hits + false_miss
        s.RecordRequest("k1", 0, wrapperHit: false, savedN: 171200);
        var r1 = s.OnPromptEval(100);
        Assert.True(r1!.Hit);
        Assert.True(r1.FalseMiss);
        Assert.False(r1.FalseHit);
        // wrapper 报 HIT + 实际未命中 → misses + false_hit
        s.RecordRequest("k2", 1, wrapperHit: true, savedN: 171200);
        var r2 = s.OnPromptEval(171856);
        Assert.False(r2!.Hit);
        Assert.True(r2.FalseHit);
        Assert.False(r2.FalseMiss);

        var snap = s.Snapshot();
        Assert.Equal(2, snap.TotalAttempts);
        Assert.Equal(1, snap.TotalHits);
        Assert.Equal(1, snap.TotalFalseMiss);
        Assert.Equal(1, snap.TotalFalseHit);
    }

    [Fact]
    public void Alert_Red_Below50Percent_StateTransitionOnly()
    {
        var s = new RestoreStats(TempPath());
        // 前 4 次：2 hit + 2 miss（样本 < 5，不评估告警）
        for (int i = 0; i < 4; i++)
        {
            s.RecordRequest($"k{i}", 0, false, 171200);
            var r = s.OnPromptEval(i % 2 == 0 ? 100 : 171856);
            Assert.Equal(RestoreStats.AlertLevel.None, r!.Alert); // 样本不足
        }
        // 第 5 次 miss → 2/5 = 40% < 50% → 红色告警（状态迁移）
        s.RecordRequest("k5", 0, false, 171200);
        var r5 = s.OnPromptEval(171856);
        Assert.Equal(RestoreStats.AlertLevel.Red, r5!.Alert);
        // 第 6 次 miss → 2/6 = 33% 仍 Red → 同级别不重复告警
        s.RecordRequest("k6", 0, false, 171200);
        var r6 = s.OnPromptEval(171856);
        Assert.Equal(RestoreStats.AlertLevel.None, r6!.Alert);
    }

    [Fact]
    public void Persistence_RoundTrip()
    {
        var path = TempPath();
        var s = new RestoreStats(path);
        s.RecordRequest("trae_global", 0, false, 171200);
        s.OnPromptEval(100); // hit
        s.Save();

        Assert.True(File.Exists(path));
        // 新实例从文件恢复累计统计
        var s2 = new RestoreStats(path);
        var snap = s2.Snapshot();
        Assert.Equal(1, snap.TotalAttempts);
        Assert.Equal(1, snap.TotalHits);
        Assert.Single(snap.ByKey);
        Assert.Equal("trae_global", snap.ByKey[0].Key);
        File.Delete(path);
    }
}
