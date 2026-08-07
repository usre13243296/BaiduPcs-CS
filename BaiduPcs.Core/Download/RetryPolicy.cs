using System.Net;

namespace BaiduPcs.Core.Download;

/// <summary>下载错误分类（对齐 Go worker.go 的状态码分类）。</summary>
public enum DownloadErrorKind
{
    /// <summary>正常。</summary>
    None,

    /// <summary>致命：直接判死，需换直链或放弃。</summary>
    Fatal,

    /// <summary>限流：退避后重连。</summary>
    RateLimited,

    /// <summary>网络错误：重连。</summary>
    Network,

    /// <summary>服务端不支持 Range（HTTP 200），需要单线程降级。</summary>
    SingleStreamFallback,
}

/// <summary>
/// HTTP 状态码 → 下载错误分类。
/// 对齐 Go worker.go:290-313：403/404/416 致命；429/509 限流；其余非 2xx 网络错误。
/// </summary>
public static class DownloadErrorClassifier
{
    public static DownloadErrorKind Classify(HttpStatusCode status) => status switch
    {
        HttpStatusCode.OK or HttpStatusCode.PartialContent => DownloadErrorKind.None,
        HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.RequestedRangeNotSatisfiable => DownloadErrorKind.Fatal,
        (HttpStatusCode)429 or (HttpStatusCode)509 => DownloadErrorKind.RateLimited,
        _ => DownloadErrorKind.Network,
    };
}

/// <summary>
/// 重试等待策略。对齐 Go internal/pcsfunctions/common.go:8-13 的线性退避（2s/4s/6s，封顶 6s）。
/// </summary>
public static class DownloadRetryPolicy
{
    /// <summary>任务级重试等待。</summary>
    public static TimeSpan NextWait(int retryCount) => retryCount switch
    {
        <= 0 => TimeSpan.Zero,
        1 => TimeSpan.FromSeconds(2),
        2 => TimeSpan.FromSeconds(4),
        _ => TimeSpan.FromSeconds(6),
    };

    /// <summary>限流（429/509）退避：第 N 次重试等待 1s/2s/4s（封顶 4s）。</summary>
    public static TimeSpan RateLimitWait(int attempt) => attempt switch
    {
        <= 0 => TimeSpan.Zero,
        1 => TimeSpan.FromSeconds(1),
        2 => TimeSpan.FromSeconds(2),
        _ => TimeSpan.FromSeconds(4),
    };

    /// <summary>网络错误重连等待：第 N 次重试等待 1s/2s/4s（封顶 4s）。</summary>
    public static TimeSpan NetworkWait(int attempt) => attempt switch
    {
        <= 0 => TimeSpan.Zero,
        1 => TimeSpan.FromSeconds(1),
        2 => TimeSpan.FromSeconds(2),
        _ => TimeSpan.FromSeconds(4),
    };
}

/// <summary>
/// 连接限流器：滑动窗口内限制新连接次数，防止高频重连触发风控。
/// 对齐 Go requester/downloader/resetcontroler.go：9s 窗口最多 80 次。
/// </summary>
public sealed class ConnectionRateLimiter
{
    private readonly object _lock = new();
    private readonly Queue<DateTime> _stamps = new();
    private readonly int _max;
    private readonly TimeSpan _window;

    public ConnectionRateLimiter(int max = 80, TimeSpan? window = null)
    {
        _max = max;
        _window = window ?? TimeSpan.FromSeconds(9);
    }

    /// <summary>尝试登记一次新连接；窗口内已满返回 false。</summary>
    public bool TryAcquire()
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            while (_stamps.Count > 0 && now - _stamps.Peek() > _window)
            {
                _stamps.Dequeue();
            }

            if (_stamps.Count >= _max)
            {
                return false;
            }

            _stamps.Enqueue(now);
            return true;
        }
    }
}
