using BaiduPcs.Core.Download;

namespace BaiduPcs.Core.Tests;

/// <summary>
/// <see cref="AsyncManualResetEvent"/> 的门控语义。
/// 重点防的是「Reset 遗弃等待者」——那会让暂停后的任务永远停在 0 B/s（v0.6.1 及更早的隐患）。
/// </summary>
public class AsyncManualResetEventTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task InitiallySignaled_DoesNotBlock()
    {
        var gate = new AsyncManualResetEvent(signaled: true);

        await gate.WaitAsync(CancellationToken.None).WaitAsync(Guard);
    }

    [Fact]
    public async Task ResetThenSet_ReleasesWaiter()
    {
        var gate = new AsyncManualResetEvent(signaled: true);
        gate.Reset();

        var waiter = gate.WaitAsync(CancellationToken.None);
        Assert.False(waiter.IsCompleted);

        gate.Set();
        await waiter.WaitAsync(Guard);
    }

    [Fact]
    public async Task DoubleReset_DoesNotStrandWaiter()
    {
        var gate = new AsyncManualResetEvent(signaled: true);

        // 第一次暂停：门关上，worker 挂上等待
        gate.Reset();
        var waiter = gate.WaitAsync(CancellationToken.None);

        // 第二次暂停（重复点暂停 / 与 worker 竞争）：旧实现会把门换掉，等待者永远醒不来
        gate.Reset();

        gate.Set();
        await waiter.WaitAsync(Guard);
    }

    [Fact]
    public async Task ManyResetsAndOneSet_ReleasesAllWaiters()
    {
        var gate = new AsyncManualResetEvent(signaled: true);
        gate.Reset();

        var waiters = Enumerable.Range(0, 8).Select(_ => gate.WaitAsync(CancellationToken.None)).ToArray();
        for (var i = 0; i < 5; i++)
        {
            gate.Reset(); // 幂等：不应影响上面的等待者
        }

        gate.Set();
        await Task.WhenAll(waiters).WaitAsync(Guard);
    }

    [Fact]
    public async Task SignaledAgainAfterSet_StillAllowsNewWaiters()
    {
        var gate = new AsyncManualResetEvent();
        gate.Set();

        // 已放行状态下 WaitAsync 应立即返回
        await gate.WaitAsync(CancellationToken.None).WaitAsync(Guard);

        // 关 → 开 一轮后仍然正常
        gate.Reset();
        var waiter = gate.WaitAsync(CancellationToken.None);
        gate.Set();
        await waiter.WaitAsync(Guard);
    }

    [Fact]
    public async Task WaitAsync_HonorsCancellation()
    {
        var gate = new AsyncManualResetEvent();
        using var cts = new CancellationTokenSource();

        var waiter = gate.WaitAsync(cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter.WaitAsync(Guard));
    }
}
