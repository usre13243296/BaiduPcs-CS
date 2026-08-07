namespace BaiduPcs.Core.Download;

/// <summary>
/// 异步手动重置事件：Set 放行所有等待者，Reset 后重新阻塞。
/// 用于暂停/恢复门控与唤醒信号。
/// </summary>
public sealed class AsyncManualResetEvent
{
    private TaskCompletionSource _tcs;

    /// <param name="signaled">初始是否放行（暂停门控初始必须放行）。</param>
    public AsyncManualResetEvent(bool signaled = false)
    {
        _tcs = New();
        if (signaled)
        {
            _tcs.TrySetResult();
        }
    }

    private static TaskCompletionSource New()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task WaitAsync(CancellationToken ct) => _tcs.Task.WaitAsync(ct);

    public void Set() => _tcs.TrySetResult();

    public void Reset() => _tcs = New();
}
