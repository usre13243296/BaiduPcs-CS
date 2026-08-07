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
    /// 获取下载直链。对应 Go 端 PrepareLocateDownload / LocateDownload。
    /// POST pcs.baidu.com/rest/2.0/pcs/file?method=locatedownload&path=...&签名参数
    /// </summary>
    public async Task<DownloadLinkInfo> GetDownloadLinkAsync(string path, CancellationToken ct = default)
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

        // 优先取 encrypt=0 的非加密链接
        if (envelope.Urls is not null)
        {
            foreach (var item in envelope.Urls)
            {
                if (item.Encrypt == 0 && !string.IsNullOrEmpty(item.Url))
                {
                    return new DownloadLinkInfo { Dlink = item.Url };
                }
            }
        }

        // 兜底：dlink 列表
        if (envelope.DlinkList is { Count: > 0 } dlinks && !string.IsNullOrEmpty(dlinks[0].Dlink))
        {
            return dlinks[0];
        }

        throw BaiduApiException.Other(OperationLocateDownload, "未找到下载链接");
    }
}