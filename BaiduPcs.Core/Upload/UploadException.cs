namespace BaiduPcs.Core.Upload;

/// <summary>上传错误分类。</summary>
public enum UploadErrorKind
{
    /// <summary>正常。</summary>
    None,

    /// <summary>致命：HTTP 400/401/403/413、空间不足、重名冲突等，重试无意义。</summary>
    Fatal,

    /// <summary>网络错误：可重试。</summary>
    Network,

    /// <summary>限流（429/509）：退避后重试。</summary>
    RateLimited,

    /// <summary>服务端上传会话失效（31363）：需重新 precreate 整体重传。</summary>
    SessionExpired,

    /// <summary>本地文件不存在或不可读。</summary>
    LocalFileError,

    /// <summary>文件超过 128GB 上限。</summary>
    TooLarge,
}

/// <summary>上传引擎异常（对齐 Download/DownloadException 的模型）。</summary>
public sealed class UploadException : Exception
{
    public UploadException(string message, UploadErrorKind kind = UploadErrorKind.Network, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
    }

    public UploadErrorKind Kind { get; }
}
