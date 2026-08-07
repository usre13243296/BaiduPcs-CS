using System.Text.Json.Serialization;
using BaiduPcs.Core.Models;

namespace BaiduPcs.Core.Api;

public sealed partial class BaiduPcsApiClient
{
    private sealed class UkEnvelope
    {
        [JsonPropertyName("records")]
        public List<UserInfo>? Records { get; set; }
    }

    private sealed class TemplateVariableEnvelope
    {
        [JsonPropertyName("result")]
        public TemplateVariableResult? Result { get; set; }
    }

    private sealed class TemplateVariableResult
    {
        [JsonPropertyName("bdstoken")]
        public string? BdsToken { get; set; }
    }

    /// <summary>
    /// 获取当前账号 UK（同时用于验证 BDUSS 是否有效）。
    /// 对应 Go 端 PrepareUK / UK()，GET https://pan.baidu.com/api/user/getinfo?need_selfinfo=1
    /// 实验确认：该接口必须使用 netdisk 客户端 UA（ApiRequestType.Pan），
    /// 使用 Chrome UA 会返回 errno=2（params error）。
    /// </summary>
    public Task<long> GetUkAsync(CancellationToken ct = default)
    {
        var url = GeneratePanUrl("user/getinfo", new Dictionary<string, string>
        {
            ["need_selfinfo"] = "1",
            ["clienttype"] = "0",
            ["app_id"] = _options.AppId.ToString(),
        });

        return SendUkAsync(url, ct);
    }

    private async Task<long> SendUkAsync(Uri url, CancellationToken ct)
    {
        var envelope = await SendJsonAsync<UkEnvelope>(
            ApiRequestType.Pan,
            OperationGetUk,
            HttpMethod.Get,
            url,
            ct: ct).ConfigureAwait(false);

        if (envelope.Records is null || envelope.Records.Count != 1)
        {
            throw BaiduApiException.Other(OperationGetUk, "未知返回数据");
        }

        return envelope.Records[0].Uk;
    }

    /// <summary>
    /// 获取 bdstoken。对应 Go 端 PrepareBDStoken / BDSToken()。
    /// GET https://pan.baidu.com/api/gettemplatevariable?clienttype=0&app_id=..&fields=["bdstoken"]
    /// 与 getinfo 同理，必须使用 netdisk UA（ApiRequestType.Pan）。
    /// </summary>
    public async Task<string> GetBdsTokenAsync(CancellationToken ct = default)
    {
        var url = GeneratePanUrl("gettemplatevariable", new Dictionary<string, string>
        {
            ["clienttype"] = "0",
            ["app_id"] = _options.AppId.ToString(),
            ["fields"] = "[\"bdstoken\"]",
        });

        var envelope = await SendJsonAsync<TemplateVariableEnvelope>(
            ApiRequestType.Pan,
            OperationGetBdsToken,
            HttpMethod.Get,
            url,
            ct: ct).ConfigureAwait(false);

        if (string.IsNullOrEmpty(envelope.Result?.BdsToken))
        {
            throw BaiduApiException.Other(OperationGetBdsToken, "未知返回数据");
        }

        return envelope.Result.BdsToken;
    }
}
