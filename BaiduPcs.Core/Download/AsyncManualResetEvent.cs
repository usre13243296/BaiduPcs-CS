namespace BaiduPcs.Core.Download;

/// <summary>
/// 异步手动重置事件：Set 放行所有等待者，Reset 后重新阻塞。
/// 用于暂停/恢复门控与唤醒信号。
/// </summary>
/// <remarks>
/// <see cref="Reset"/> 是**幂等**的：只有当前处于「已放行」状态时才换新的门。
/// 旧实现无条件把 TCS 换成新对象，会遗弃已经等到旧门上的等待者——连续两次暂停
/// （或暂停与 worker 竞争）就会让 worker 永远挂在旧门上，任务表现为「进行中但 0 B/s」；
/// 监控循环每 500ms 调一次 Reset 也会不断遗弃 <c>NeedWork</c> 的等待者。
/// </remarks>
public sealed class AsyncManualResetEvent
{
    private readonly object _sync = new();
    private TaskCompletionSource _tcs = New();

    /// <param name="signaled">初始是否放行（暂停门控初始必须放行）。</param>
    public AsyncManualResetEvent(bool signaled = false)
    {
        if (signaled)
        {
            _tcs.TrySetResult();
        }
    }

    private static TaskCompletionSource New()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task WaitAsync(CancellationToken ct)
    {
        Task gate;
        lock (_sync)
        {
            // 未放行的门永远不会被 Reset 替换，所以这个引用是稳定的
            gate = _tcs.Task;
        }

        return gate.WaitAsync(ct);
    }

    public void Set()
    {
        lock (_sync)
        {
            _tcs.TrySetResult();
        }
    }

    /// <summary>重新阻塞。已经处于阻塞状态时是空操作（不替换门，等待者保持有效）。</summary>
    public void Reset()
    {
        lock (_sync)
        {
            if (!_tcs.Task.IsCompleted)
            {
                return;
            }

            _tcs = New();
        }
    }
}
