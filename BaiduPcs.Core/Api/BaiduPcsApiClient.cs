using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BaiduPcs.Core.Auth;
using BaiduPcs.Core.Infrastructure;
using BaiduPcs.Core.Models;

namespace BaiduPcs.Core.Api;

/// <summary>API 请求使用的 UA 类型（对应 Go 端 reqType）。</summary>
public enum ApiRequestType
{
    /// <summary>PCS 接口（pcs.baidu.com/rest/2.0/pcs/...）</summary>
    Pcs,

    /// <summary>Pan 接口（pan.baidu.com/api/... 等）</summary>
    Pan,
}

/// <summary>ApiClient 选项（对应 Go 端 BaiduPCS 的 appID/https/UA 配置）。</summary>
public sealed class BaiduPcsApiOptions
{
    public int AppId { get; set; } = ApiConstants.DefaultAppId;
    public bool Https { get; set; } = true;
    public string PcsAddr { get; set; } = ApiConstants.PcsBaiduCom;
    public string PcsUserAgent { get; set; } = ApiConstants.DefaultUserAgent;
    public string PanUserAgent { get; set; } = ApiConstants.NetdiskUa;
}

/// <summary>
/// 百度网盘 API 客户端：URL 构造、请求发送、响应解析与错误映射。
/// 对应 Go 端 baidupcs 包，但采用 async/await 与统一异常模型。
/// </summary>
public sealed partial class BaiduPcsApiClient
{
    public const string OperationGetUk = "获取UK";
    public const string OperationGetBdsToken = "获取bdstoken";
    public const string OperationQuotaInfo = "获取当前用户空间配额信息";
    public const string OperationFilesDirectoriesMeta = "获取文件/目录的元信息";
    public const string OperationFilesDirectoriesList = "获取目录下的文件列表";
    public const string OperationSearch = "搜索";
    public const string OperationRemove = "删除文件/目录";
    public const string OperationMkdir = "创建目录";
    public const string OperationRename = "重命名文件/目录";
    public const string OperationCopy = "拷贝文件/目录";
    public const string OperationMove = "移动文件/目录";
    public const string OperationLocateDownload = "获取下载链接";

    private readonly BaiduHttpClient _http;
    private readonly BaiduSession _session;
    private readonly BaiduPcsApiOptions _options;
    private readonly JsonSerializerOptions _jsonOptions;

    public BaiduPcsApiClient(
        BaiduHttpClient http,
        BaiduSession session,
        BaiduPcsApiOptions? options = null)
    {
        _http = http;
        _session = session;
        _options = options ?? new BaiduPcsApiOptions();
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
        };
    }

    public BaiduPcsApiOptions Options => _options;
    public BaiduSession Session => _session;

    // ======================== URL 构造 ========================

    /// <summary>
    /// PCS URL：/rest/2.0/pcs/{subPath}?app_id=..&amp;method=..&amp;{params}
    /// 对应 Go 端 generatePCSURL。
    /// </summary>
    public Uri GeneratePcsUrl(string subPath, string method, IReadOnlyDictionary<string, string>? param = null)
    {
        var builder = new UriBuilder
        {
            Scheme = _options.Https ? "https" : "http",
            Host = string.IsNullOrEmpty(_options.PcsAddr) ? ApiConstants.PcsBaiduCom : _options.PcsAddr,
            Path = "/rest/2.0/pcs/" + subPath,
        };

        var query = System.Web.HttpUtility.ParseQueryString(string.Empty);
        query["app_id"] = _options.AppId.ToString();
        query["method"] = method;
        AddParams(query, param);
        builder.Query = query.ToString();
        return builder.Uri;
    }

    /// <summary>
    /// PCS URL2：pan.baidu.com/rest/2.0/{subPath}?method=..&amp;{params}
    /// 对应 Go 端 generatePCSURL2。
    /// </summary>
    public Uri GeneratePcsUrl2(string subPath, string method, IReadOnlyDictionary<string, string>? param = null)
    {
        var builder = new UriBuilder
        {
            Scheme = _options.Https ? "https" : "http",
            Host = ApiConstants.PanBaiduCom,
            Path = "/rest/2.0/" + subPath,
        };

        var query = System.Web.HttpUtility.ParseQueryString(string.Empty);
        query["method"] = method;
        AddParams(query, param);
        builder.Query = query.ToString();
        return builder.Uri;
    }

    /// <summary>
    /// Pan URL：pan.baidu.com/api/{subPath}?{params}
    /// 对应 Go 端 generatePanURL。
    /// </summary>
    public Uri GeneratePanUrl(string subPath, IReadOnlyDictionary<string, string>? param = null)
    {
        var builder = new UriBuilder
        {
            Scheme = _options.Https ? "https" : "http",
            Host = ApiConstants.PanBaiduCom,
            Path = "/api/" + subPath,
        };

        if (param is not null)
        {
            var query = System.Web.HttpUtility.ParseQueryString(string.Empty);
            AddParams(query, param);
            builder.Query = query.ToString();
        }

        return builder.Uri;
    }

    private static void AddParams(System.Collections.Specialized.NameValueCollection query, IReadOnlyDictionary<string, string>? param)
    {
        if (param is null)
        {
            return;
        }

        foreach (var (key, value) in param)
        {
            query[key] = value;
        }
    }
    // ======================== 请求管道 ========================

    private async Task<string> SendRawAsync(
        ApiRequestType requestType,
        string operation,
        HttpMethod method,
        Uri url,
        HttpContent? content = null,
        IReadOnlyDictionary<string, string>? headers = null,
        CancellationToken ct = default)
    {
        var userAgent = requestType == ApiRequestType.Pcs ? _options.PcsUserAgent : _options.PanUserAgent;
        var request = BaiduRequestFactory.Create(method, url, userAgent, _session.BuildCookieHeader(), headers);
        if (content is not null)
        {
            request.Content = content;
        }

        HttpResponseMessage? response = null;
        try
        {
            response = await _http.SendAsync(request, ct).ConfigureAwait(false);

            // 吸收服务器下发的 cookie（如 BAIDUID、STOKEN），供后续请求使用
            _session.AbsorbCookiesFromResponse(response);

            // 以字节读取并显式 UTF-8 解码，避免依赖响应头 charset（百度常返回非标准 "utf8"，
            // 会导致 ReadAsStringAsync 抛 'utf8' is not a supported encoding name 异常）。
            var body = await ReadAsUtf8StringAsync(response, ct).ConfigureAwait(false);

            if ((int)response.StatusCode >= 400)
            {
                TryThrowRemoteError(requestType, operation, body, response.StatusCode);
            }

            return body;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw BaiduApiException.NetError(operation, new TimeoutException("请求超时"));
        }
        catch (HttpRequestException ex)
        {
            throw BaiduApiException.NetError(operation, ex);
        }
        finally
        {
            response?.Dispose();
        }
    }

    private async Task<T> SendJsonAsync<T>(
        ApiRequestType requestType,
        string operation,
        HttpMethod method,
        Uri url,
        HttpContent? content = null,
        IReadOnlyDictionary<string, string>? headers = null,
        CancellationToken ct = default)
    {
        var body = await SendRawAsync(requestType, operation, method, url, content, headers, ct).ConfigureAwait(false);

        try
        {
            using var doc = JsonDocument.Parse(body);
            CheckError(requestType, operation, doc.RootElement);
            return JsonSerializer.Deserialize<T>(doc.RootElement.GetRawText(), _jsonOptions)
                ?? throw new JsonException("反序列化结果为 null");
        }
        catch (JsonException ex)
        {
            throw BaiduApiException.JsonParseError(operation, ex);
        }
    }

    /// <summary>
    /// 以字节流读取响应体并显式按 UTF-8 解码，规避响应头 charset 为非标准值（如 "utf8"）
    /// 时 ReadAsStringAsync 抛 "'utf8' is not a supported encoding name" 的异常。
    /// </summary>
    private static async Task<string> ReadAsUtf8StringAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>
    /// 检查 JSON 响应中的错误字段（errno / error_code），非 0 则抛异常。
    /// 对应 Go 端 pcserror.HandleJSONParse 的远端错误判定。
    /// </summary>
    internal static void CheckError(ApiRequestType requestType, string operation, JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw BaiduApiException.Other(operation, "响应不是 JSON 对象");
        }

        if (root.TryGetProperty("errno", out var errnoElement)
            && errnoElement.ValueKind is JsonValueKind.Number
            && errnoElement.TryGetInt32(out var errno)
            && errno != 0)
        {
            throw BaiduApiException.PanRemote(operation, errno);
        }

        if (root.TryGetProperty("error_code", out var errorCodeElement)
            && errorCodeElement.ValueKind is JsonValueKind.Number
            && errorCodeElement.TryGetInt32(out var errorCode)
            && errorCode != 0)
        {
            var errorMsg = root.TryGetProperty("error_msg", out var errorMsgElement)
                && errorMsgElement.ValueKind == JsonValueKind.String
                ? errorMsgElement.GetString() ?? string.Empty
                : string.Empty;
            throw BaiduApiException.PcsRemote(operation, errorCode, errorMsg);
        }
    }

    /// <summary>HTTP 4xx/5xx 时尝试从响应体解析错误并抛出。</summary>
    private static void TryThrowRemoteError(ApiRequestType requestType, string operation, string body, System.Net.HttpStatusCode statusCode)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            CheckError(requestType, operation, doc.RootElement);
        }
        catch (JsonException)
        {
            // 响应体不是 JSON，走通用远端错误
        }

        throw BaiduApiException.Other(operation, $"http 响应错误, 状态码: {(int)statusCode}");
    }

    /// <summary>构造 multipart 表单，含一个无 Content-Type 的 param 字段（精确对齐 Go 端 multipartreader）。</summary>
    internal static HttpContent CreateParamMultipart(string json)
        => new ParamMultipartContent(json);

    /// <summary>构造 x-www-form-urlencoded 表单。</summary>
    internal static FormUrlEncodedContent CreateFormUrlEncoded(IEnumerable<KeyValuePair<string, string>> fields)
        => new(fields);
}
