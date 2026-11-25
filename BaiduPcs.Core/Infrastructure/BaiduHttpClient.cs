using System.Net;
using System.Net.Http.Headers;

namespace BaiduPcs.Core.Infrastructure;

/// <summary>
/// 百度网盘 HTTP 客户端配置。
/// </summary>
public sealed class BaiduHttpOptions
{
    /// <summary>每个服务器最大并发连接数（多线程下载/上传时关键）</summary>
    public int MaxConnectionsPerServer { get; set; } = 16;

    /// <summary>连接超时</summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>整体请求超时（与 Go 端一致，默认 50s）</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(50);

    /// <summary>HTTP 代理地址（如 http://127.0.0.1:7890），空表示不设置</summary>
    public string? ProxyAddress { get; set; }

    /// <summary>
    /// 是否忽略 TLS 证书校验（仅用于企业代理自签证书等特殊场景）。
    /// 默认 <c>false</c>：本客户端每个请求都携带 BDUSS / STOKEN 等登录凭证，
    /// 关闭校验等于把账号交给链路上的任何人（公共 Wi-Fi、恶意代理）。
    /// 如需关闭，请在调用方显式设置为 <c>true</c> 并让用户知情。
    /// </summary>
    public bool BypassSslValidation { get; set; }
}

/// <summary>
/// 基于 SocketsHttpHandler 的 HTTP 客户端封装。
/// 对应 Go 端 requester.HTTPClient，但采用每请求显式携带 cookie/UA 的模型。
/// </summary>
public sealed class BaiduHttpClient : IDisposable
{
    private readonly HttpClient _client;

    public BaiduHttpClient(BaiduHttpOptions? options = null, HttpMessageHandler? handler = null)
    {
        options ??= new BaiduHttpOptions();

        handler ??= BuildHandler(options);

        _client = new HttpClient(handler)
        {
            Timeout = options.Timeout,
        };
    }

    private static SocketsHttpHandler BuildHandler(BaiduHttpOptions options)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
            MaxConnectionsPerServer = options.MaxConnectionsPerServer,
            ConnectTimeout = options.ConnectTimeout,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            UseProxy = options.ProxyAddress is not null,
        };

        if (options.ProxyAddress is not null)
        {
            handler.Proxy = new System.Net.WebProxy(options.ProxyAddress);
        }

        if (options.BypassSslValidation)
        {
            handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        }

        return handler;
    }

    /// <summary>发送请求（不自动携带 cookie，由调用方构造请求头）。</summary>
    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => _client.SendAsync(request, cancellationToken);

    public void Dispose() => _client.Dispose();
}

/// <summary>请求构造小助手：为 ApiClient 拼装带 UA/Cookie 的请求。</summary>
public static class BaiduRequestFactory
{
    public static HttpRequestMessage Create(
        HttpMethod method,
        Uri url,
        string userAgent,
        string? cookieHeader,
        IReadOnlyDictionary<string, string>? extraHeaders = null)
    {
        var request = new HttpRequestMessage(method, url);
        // 必须用 TryAddWithoutValidation 原样发送 UA：netdisk;P2SP;... 是非标准格式，
        // UserAgent.TryParseAdd 会解析失败并静默丢弃，导致 UA 头缺失被百度拒绝(errno=2 params error)。
        request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        if (!string.IsNullOrEmpty(cookieHeader))
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        }

        if (extraHeaders is not null)
        {
            foreach (var (key, value) in extraHeaders)
            {
                request.Headers.TryAddWithoutValidation(key, value);
            }
        }

        return request;
    }
}
