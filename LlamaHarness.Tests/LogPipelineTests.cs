using LlamaHarness;
using Xunit;

namespace LlamaHarness.Tests;

/// <summary>
/// 统一异步日志管道纯逻辑单测（批次 1）：
/// - BoundedLineQueue：DropNewest/DropOldest 满时丢弃 + 计数、FIFO 保序
/// - FlushPolicy：时间/大小双阈值边界
/// - LogStreamWriter：轮切触发/不触发
/// - 高并发多线程 Enqueue：单流内部 FIFO 保序
/// 注：写线程集成行为（IO 退避 / Shutdown drain / e2e 落盘）见批次 3 集成测试。
/// </summary>
public class LogPipelineTests
{
    private static LogMessage Msg(int seq, LogStream stream = LogStream.Main) =>
        new(stream, DateTime.UtcNow, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] line-{seq}", $"line-{seq}");

    // ==================== BoundedLineQueue ====================

    [Fact]
    public void Queue_DropNewest_WhenFull_KeepsOldestAndCounts()
    {
        var q = new BoundedLineQueue(3) { Policy = QueueFullPolicy.DropNewest };
        Assert.True(q.TryEnqueue(Msg(1)));
        Assert.True(q.TryEnqueue(Msg(2)));
        Assert.True(q.TryEnqueue(Msg(3)));
        // 满：新入队被丢弃，历史保留
        Assert.False(q.TryEnqueue(Msg(4)));
        Assert.Equal(3, q.Count);
        Assert.Equal(1, q.TakeDroppedDelta());

        var batch = new List<LogMessage>(8);
        q.Drain(batch, 8);
        Assert.Equal(3, batch.Count);
        Assert.Equal("line-1", batch[0].RawLine); // 最旧仍在队首
    }

    [Fact]
    public void Queue_DropOldest_WhenFull_ReplacesOldest()
    {
        var q = new BoundedLineQueue(3) { Policy = QueueFullPolicy.DropOldest };
        q.TryEnqueue(Msg(1));
        q.TryEnqueue(Msg(2));
        q.TryEnqueue(Msg(3));
        Assert.True(q.TryEnqueue(Msg(4))); // 挤掉 line-1
        Assert.Equal(3, q.Count);
        Assert.Equal(1, q.TakeDroppedDelta());

        var batch = new List<LogMessage>(8);
        q.Drain(batch, 8);
        Assert.Equal("line-2", batch[0].RawLine); // line-1 被挤掉
    }

    [Fact]
    public void Queue_FifoOrdering()
    {
        var q = new BoundedLineQueue(100);
        for (int i = 0; i < 50; i++) q.TryEnqueue(Msg(i));
        var batch = new List<LogMessage>(64);
        q.Drain(batch, 10);
        Assert.Equal(10, batch.Count);
        for (int i = 0; i < 10; i++)
            Assert.Equal($"line-{i}", batch[i].RawLine); // 严格 FIFO
    }

    [Fact]
    public void Queue_DroppedDelta_ResetsAfterTake()
    {
        var q = new BoundedLineQueue(1) { Policy = QueueFullPolicy.DropNewest };
        q.TryEnqueue(Msg(1));
        q.TryEnqueue(Msg(2)); // dropped
        Assert.Equal(1, q.TakeDroppedDelta());
        Assert.Equal(0, q.TakeDroppedDelta()); // 增量语义：取后归零
    }

    // ==================== FlushPolicy 双阈值边界 ====================

    [Theory]
    [InlineData(149, 0, false)]   // 时间未到、大小未到 → 不刷
    [InlineData(150, 0, true)]    // 时间阈值（≥150ms）→ 刷
    [InlineData(0, 63_999, false)]// 大小未到 → 不刷
    [InlineData(0, 64_000, true)] // 大小阈值（≥64KB）→ 刷
    public void FlushPolicy_ShouldFlush_Boundaries(long elapsedMs, long bytes, bool expected)
    {
        Assert.Equal(expected, FlushPolicy.ShouldFlush(elapsedMs, bytes));
    }

    // ==================== LogStreamWriter 轮切边界 ====================

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "llama_harness_logtest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Rotate_TriggersAtThreshold()
    {
        var dir = TempDir();
        var path = Path.Combine(dir, "t.log");
        var w = new LogStreamWriter(path);
        w.Write(new string('a', 100));
        Assert.False(w.RotateIfNeeded(100)); // 恰好 100，未超限 → 不轮切
        w.Write("x");                        // 超 1 字节
        Assert.True(w.RotateIfNeeded(100));  // 触发轮切
        Assert.True(File.Exists(path + ".1"));
        w.Write("after-rotate");             // 自动重开新文件
        Assert.True(File.Exists(path));
        w.Dispose();
    }

    [Fact]
    public void Rotate_DoesNothingBelowThreshold()
    {
        var dir = TempDir();
        var path = Path.Combine(dir, "t.log");
        var w = new LogStreamWriter(path);
        w.Write(new string('a', 50));
        Assert.False(w.RotateIfNeeded(100));
        Assert.False(File.Exists(path + ".1"));
        w.Dispose();
    }

    // ==================== 高并发：单流内部 FIFO 保序 ====================

    [Fact]
    public void Concurrent_Enqueue_SingleStreamFifoPreserved()
    {
        // 并发语义：每个生产者自己的子序列严格递增（单流 FIFO）；跨线程交错顺序不保证。
        const int threads = 8;
        const int perThread = 500;
        var q = new BoundedLineQueue(threads * perThread);
        var ts = new Thread[threads];
        for (int t = 0; t < threads; t++)
        {
            int threadId = t;
            ts[t] = new Thread(() =>
            {
                for (int i = 0; i < perThread; i++)
                    q.TryEnqueue(Msg(threadId * perThread + i)); // seq 编码 (threadId, i)
            });
            ts[t].Start();
        }
        foreach (var t in ts) t.Join();

        var batch = new List<LogMessage>(1024);
        var lastIdx = new int[threads]; // 每生产者最后见到的 i
        Array.Fill(lastIdx, -1);
        var count = new int[threads];
        int total = 0;
        while (q.Drain(batch, 1024) > 0)
        {
            foreach (var m in batch)
            {
                int seq = int.Parse(m.RawLine.Substring("line-".Length));
                int tid = seq / perThread;
                int idx = seq % perThread;
                Assert.True(idx > lastIdx[tid], $"生产者 {tid} 子序列乱序：{idx} <= {lastIdx[tid]}");
                lastIdx[tid] = idx;
                count[tid]++;
                total++;
            }
            batch.Clear();
        }
        Assert.Equal(threads * perThread, total);
        for (int t = 0; t < threads; t++)
            Assert.Equal(perThread, count[t]); // 无丢失
    }
}
