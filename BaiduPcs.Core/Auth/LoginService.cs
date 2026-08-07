using BaiduPcs.Core.Api;
using BaiduPcs.Core.Infrastructure;
using BaiduPcs.Core.Models;

namespace BaiduPcs.Core.Auth;

/// <summary>
/// 登录服务：以 BDUSS（可附加完整 cookie 串）登录并验证。
/// 验证成功即持久化账号并设为当前账号。
/// </summary>
public sealed class LoginService
{
    private readonly BaiduHttpClient _http;
    private readonly BaiduPcsApiOptions _apiOptions;
    private readonly AccountStore _store;

    public LoginService(
        BaiduHttpClient http,
        AccountStore store,
        BaiduPcsApiOptions? apiOptions = null)
    {
        _http = http;
        _store = store;
        _apiOptions = apiOptions ?? new BaiduPcsApiOptions();
    }

    /// <summary>
    /// 通过 BDUSS 登录。cookieString 可选（形如 "BDUSS=..; STOKEN=.."）。
    /// </summary>
    public async Task<BaiduAccount> LoginByBdussAsync(
        string bduss,
        string? cookieString = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(bduss))
        {
            throw new ArgumentException("BDUSS 不能为空", nameof(bduss));
        }

        var session = BaiduSession.FromBduss(bduss.Trim());
        if (!string.IsNullOrWhiteSpace(cookieString))
        {
            session.SetCookiesFromString(cookieString);
        }

        var api = new BaiduPcsApiClient(_http, session, _apiOptions);

        // 1. 先访问网盘首页初始化 cookie（收集 BAIDUID/STOKEN 等服务器下发 cookie），
        //    再调用 getinfo 获取 UK，避免裸 BDUSS 请求被风控拒绝（errno 2）。
        var home = new PanHomeClient(_http);
        PanHomeInfo? homeInfo = null;
        try
        {
            homeInfo = await home.FetchAsync(session, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // 首页访问失败不阻断登录，继续尝试 getinfo
        }

        // 2. 验证 BDUSS 并获取 UID
        var uid = await api.GetUkAsync(ct).ConfigureAwait(false);

        // 3. 从首页获取用户名（失败不阻断登录）
        string? userName = homeInfo?.CookieValid == true ? homeInfo.UserName : null;

        var account = new BaiduAccount
        {
            Uid = uid,
            Name = string.IsNullOrWhiteSpace(userName) ? $"用户{uid}" : userName!,
            Bduss = bduss.Trim(),
            Cookies = cookieString?.Trim() ?? string.Empty,
            LastLoginAt = DateTime.UtcNow,
        };

        // 将会话完整 cookie（含服务器下发的 BAIDUID/STOKEN 等）合并回账号并持久化，
        // 确保应用重启/重建会话后仍携带完整 cookie 上下文，避免再次被风控拒绝（errno 2）。
        ApplySessionCookiesToAccount(account, session);

        _store.Upsert(account);
        return account;
    }

    private static void ApplySessionCookiesToAccount(BaiduAccount account, BaiduSession session)
    {
        var parts = new List<string>();
        foreach (var (name, value) in session.Cookies)
        {
            parts.Add($"{name}={value}");
            switch (name.ToUpperInvariant())
            {
                case "STOKEN" when string.IsNullOrEmpty(account.Stoken):
                    account.Stoken = value;
                    break;
                case "PTOKEN" when string.IsNullOrEmpty(account.Ptoken):
                    account.Ptoken = value;
                    break;
                case "SBOXTKN" when string.IsNullOrEmpty(account.Sboxtkn):
                    account.Sboxtkn = value;
                    break;
                case "BAIDUID" when string.IsNullOrEmpty(account.Baiduid):
                    account.Baiduid = value;
                    break;
            }
        }

        // 保存完整 cookie 串，供 ToSession() 完整还原会话上下文
        account.Cookies = string.Join("; ", parts);
    }
}
