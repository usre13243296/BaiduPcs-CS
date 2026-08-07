using System.Text.RegularExpressions;
using BaiduPcs.Core.Auth;
using BaiduPcs.Core.Infrastructure;

namespace BaiduPcs.Core.Api;

/// <summary>网盘首页解析结果。</summary>
public sealed class PanHomeInfo
{
    public bool CookieValid { get; set; }
    public string? Sign1 { get; set; }
    public string? Sign3 { get; set; }
    public string? Timestamp { get; set; }
    public string? UserName { get; set; }
}

/// <summary>
/// 解析 pan.baidu.com/disk/home 首页：提取 sign1/sign3/timestamp 及用户名。
/// 对应 Go 端 baidupcs/internal/panhome 包。
/// </summary>
public sealed class PanHomeClient
{
    // 对应 Go 端 signInfoRE
    private static readonly Regex SignInfoRegex = new(
        "\"sign1\":\"(.*?)\"[\\s\\S]*\"sign3\":\"(.*?)\",\"timestamp\":(\\d*?),",
        RegexOptions.Compiled);

    private static readonly Regex UserNameRegex = new(
        "\"(?:uname|user_name|username)\":\"([^\"]*)\"",
        RegexOptions.Compiled);

    private const string PanHomeUserAgent = "Mozilla/5.0";

    private readonly BaiduHttpClient _http;

    public PanHomeClient(BaiduHttpClient http)
    {
        _http = http;
    }

    /// <summary>
    /// 获取首页信息。BDUSS 无效时 CookieValid=false。
    /// </summary>
    public async Task<PanHomeInfo> FetchAsync(BaiduSession session, CancellationToken ct = default)
    {
        var request = BaiduRequestFactory.Create(
            HttpMethod.Get,
            new Uri("https://pan.baidu.com/disk/home"),
            PanHomeUserAgent,
            session.BuildCookieHeader());

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);

        // 吸收首页下发的 cookie（如 BAIDUID、STOKEN），供后续 API 请求使用
        session.AbsorbCookiesFromResponse(response);

        var finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? string.Empty;

        var info = new PanHomeInfo
        {
            // 无效 cookie 会被重定向到 passport.baidu.com
            CookieValid = !finalUrl.Contains("passport.baidu.com", StringComparison.OrdinalIgnoreCase),
        };

        if (!info.CookieValid)
        {
            return info;
        }

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        var signMatch = SignInfoRegex.Match(body);
        if (signMatch.Success)
        {
            info.Sign1 = signMatch.Groups[1].Value;
            info.Sign3 = signMatch.Groups[2].Value;
            info.Timestamp = signMatch.Groups[3].Value;
        }

        var nameMatch = UserNameRegex.Match(body);
        if (nameMatch.Success)
        {
            info.UserName = nameMatch.Groups[1].Value;
        }

        return info;
    }
}
