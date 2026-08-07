namespace BaiduPcs.Core.Download;

/// <summary>下载引擎异常。Kind 为 Fatal 时调用方应尝试换直链。</summary>
public sealed class DownloadException : Exception
{
    public DownloadException(string message, DownloadErrorKind kind = DownloadErrorKind.Network, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
    }

    /// <summary>错误分类。</summary>
    public DownloadErrorKind Kind { get; }
}
