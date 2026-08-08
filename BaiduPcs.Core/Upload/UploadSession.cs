using System.Collections.Concurrent;

namespace BaiduPcs.Core.Upload;

/// <summary>
/// 上传任务会话（进程内断点续传）：服务端 uploadid + 已成功上传分片的 md5 集合。
/// 暂停/失败后重试时复用：precreate 带 uploadid 续传、已传分片跳过；
/// 服务端会话失效（31363）时由 Uploader 清空重来。
/// 生命周期由任务层持有（跨 Uploader 实例存活，进程内有效）。
/// </summary>
public sealed class UploadSession
{
    /// <summary>precreate 返回的会话 ID；null 表示尚无会话（首次或已失效）。</summary>
    public string? UploadId { get; set; }

    /// <summary>已成功上传的分片（partseq → 服务端 md5）。</summary>
    public ConcurrentDictionary<int, string> UploadedBlocks { get; } = new();
}
