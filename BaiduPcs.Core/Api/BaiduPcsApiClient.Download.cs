using System.Text.Json.Serialization;
using BaiduPcs.Core.Crypto;

namespace BaiduPcs.Core.Api;

public sealed partial class BaiduPcsApiClient
{
    /// <summary>下载直链信息。</summary>
    public sealed class DownloadLinkInfo
    {
        public string? Dlink { get; set; }
        public string? FsId { get; set; }
    }

    private sealed class LocateDownloadEnvelope
    {
        [JsonPropertyName("urls")]
        public List<DownloadUrlItem>? Urls { get; set; }

        [JsonPropertyName("dlink")]
        public List<DownloadLinkInfo>? DlinkList { get; set; }
    }

    private sealed class DownloadUrlItem
    {
        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("encrypt")]
        public int Encrypt { get; set; }
    }

    /// <summary>
    /// 发送原始下载请求（用于直链文件流），自动附带会话 cookie 与 netdisk UA。
    /// </summary>
    public async Task<HttpResponseMessage> SendDirectAsync(HttpRequestMessage request, CancellationToken ct = default)
    {
        request.Headers.TryAddWithoutValidation("User-Agent", _options.PanUserAgent);
        var cookie = _session.BuildCookieHeader();
        if (!string.IsNullOrEmpty(cookie))
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
        }

        return await _http.SendAsync(request, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 构造下载候选直链列表：locatedownload 签名直链（多备选）+ method=download 官方通道兜底。
    /// 引擎按序尝试：dlink 全部失败（403 等）后自动降级官方通道。
    /// 对应 Go 端 LocateDownload + DlinkPrefer + DownloadFile 降级。
    /// </summary>
    public async Task<IReadOnlyList<string>> BuildDownloadUrlsAsync(string path, CancellationToken ct = default)
    {
        var urls = new List<string>();
        try
        {
            urls.AddRange(await GetDownloadLinksAsync(path, ct).ConfigureAwait(false));
        }
        catch (BaiduApiException)
        {
            // locatedownload 失败（UID 缺失 / 风控 / 接口变更）：仅走官方通道兜底
        }

        // 官方通道（method=download，服务端按账号等级签发直链并 302 重定向）作为最终兜底
        urls.Add(GeneratePcsUrl("file", "download", new Dictionary<string, string>
        {
            ["path"] = path,
        }).ToString());
        return urls;
    }

    /// <summary>
    /// 获取 locatedownload 签名直链列表（多备选链接，encrypt=0 且跳过 nb.cache 主机）。
    /// 对应 Go 端 PrepareLocateDownload：urls[] 只取 encrypt==0 的链接；
    /// nb.cache 开头的备选 CDN 主机不稳定，跳过（对齐 Go DlinkPrefer）。
    /// </summary>
    public async Task<IReadOnlyList<string>> GetDownloadLinksAsync(string path, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(path))
        {
            throw new System.ArgumentException("path 不能为空", nameof(path));
        }

        var bduss = _session.Bduss ?? string.Empty;
        if (_session.Uid <= 0)
        {
            throw BaiduApiException.Other(OperationLocateDownload, "缺失 UID，无法生成下载签名");
        }

        var sign = NetdiskSign.LocateDownloadUrlParam(
            _session.Uid,
            bduss,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        var query = System.Web.HttpUtility.ParseQueryString(string.Empty);
        query["ant"] = "1";
        query["check_blue"] = "1";
        query["es"] = "1";
        query["esl"] = "1";
        query["app_id"] = ApiConstants.PanAppId;
        query["method"] = "locatedownload";
        query["path"] = path;
        query["ver"] = "4.0";
        query["clienttype"] = "17";
        query["channel"] = "0";
        query["apn_id"] = "1_0";
        query["freeisp"] = "0";
        query["queryfree"] = "0";
        query["use"] = "0";

        var builder = new UriBuilder
        {
            Scheme = _options.Https ? "https" : "http",
            Host = string.IsNullOrEmpty(_options.PcsAddr) ? ApiConstants.PcsBaiduCom : _options.PcsAddr,
            Path = "/rest/2.0/pcs/file",
        };
        builder.Query = query.ToString() + "&" + sign;

        var envelope = await SendJsonAsync<LocateDownloadEnvelope>(
            ApiRequestType.Pan,
            OperationLocateDownload,
            HttpMethod.Post,
            builder.Uri,
            ct: ct).ConfigureAwait(false);

        var result = new List<string>();
        if (envelope.Urls is not null)
        {
            foreach (var item in envelope.Urls)
            {
                if (item.Encrypt != 0 || string.IsNullOrEmpty(item.Url))
                {
                    continue;
                }

                if (IsNbCacheHost(item.Url))
                {
                    continue;
                }

                result.Add(item.Url);
            }
        }

        // 兜底：dlink 列表
        if (envelope.DlinkList is not null)
        {
            foreach (var d in envelope.DlinkList)
            {
                if (!string.IsNullOrEmpty(d.Dlink))
                {
                    result.Add(d.Dlink);
                }
            }
        }

        if (result.Count == 0)
        {
            throw BaiduApiException.Other(OperationLocateDownload, "未找到下载链接");
        }

        return result;
    }

    /// <summary>
    /// 获取下载直链（取第一个可用备选链接）。供"复制链接"等单链接场景使用。
    /// </summary>
    public async Task<DownloadLinkInfo> GetDownloadLinkAsync(string path, CancellationToken ct = default)
    {
        var links = await GetDownloadLinksAsync(path, ct).ConfigureAwait(false);
        return new DownloadLinkInfo { Dlink = links[0] };
    }

    /// <summary>判断是否为 nb.cache 开头的备选 CDN 主机（不稳定，跳过）。</summary>
    private static bool IsNbCacheHost(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.Host.StartsWith("nb.cache", StringComparison.OrdinalIgnoreCase);
    }
}
