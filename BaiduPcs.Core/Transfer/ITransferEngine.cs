namespace BaiduPcs.Core.Transfer;

/// <summary>
/// 传输引擎统一接口（下载/上传共用）：任务层通过该接口路由暂停/恢复，
/// 取消走任务层的 CancellationTokenSource。
/// </summary>
public interface ITransferEngine
{
    /// <summary>暂停：引擎在分片边界挂起。</summary>
    Task PauseAsync();

    /// <summary>恢复：放行挂起的分片。</summary>
    Task ResumeAsync();
}
