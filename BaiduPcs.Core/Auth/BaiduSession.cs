namespace BaiduPcs.Core.Auth;

/// <summary>
/// 百度网盘会话：持有登录凭据（BDUSS 等 cookie）与用户信息。
/// 对应 Go 端 Baidu 账号对象 + baidupcs.NewPCS/SetStoken 的组合。
/// </summary>
public sealed class BaiduSession
{
    private readonly List<KeyValuePair<string, string>> _cookies = new();

    /// <summary>百度 uid（位于下载签名时需要）</summary>
    public long Uid { get; set; }

    /// <summary>用户名（昵称）</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>网盘工作目录（GUI 下仅作为默认路径使用）</summary>
    public string Workdir { get; set; } = "/";

    /// <summary>可选的 bdstoken（部分接口需要）</summary>
    public string? BdsToken { get; set; }

    /// <summary>当前 cookie 列表（有序）</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Cookies => _cookies;

    /// <summary>BDUSS 值；未设置时为 null</summary>
    public string? Bduss => GetCookie("BDUSS");

    /// <summary>以 BDUSS 创建会话（后续可用 SetCookie 补充 STOKEN 等）。</summary>
    public static BaiduSession FromBduss(string bduss)
    {
        var session = new BaiduSession();
        session.SetCookie("BDUSS", bduss);
        return session;
    }

    /// <summary>
    /// 设置 cookie（同名覆盖）。名称为空或值包含非法字符时忽略。
    /// </summary>
    public void SetCookie(string name, string value)
    {
        if (string.IsNullOrWhiteSpace(name) || value.Contains(';', StringComparison.Ordinal))
        {
            return;
        }

        for (var i = 0; i < _cookies.Count; i++)
        {
            if (string.Equals(_cookies[i].Key, name, StringComparison.OrdinalIgnoreCase))
            {
                _cookies[i] = new KeyValuePair<string, string>(name, value);
                return;
            }
        }

        _cookies.Add(new KeyValuePair<string, string>(name, value));
    }

    /// <summary>从形如 "BDUSS=xxx; STOKEN=yyy" 的 cookie 串设置多个 cookie。</summary>
    public void SetCookiesFromString(string cookieString)
    {
        foreach (var (name, value) in CookieParser.Parse(cookieString))
        {
            SetCookie(name, value);
        }
    }

    /// <summary>
    /// 从 HTTP 响应头中吸收 Set-Cookie，使后续请求自动携带服务器下发的 cookie
    /// （如 BAIDUID、STOKEN 等）。对齐 Go 端 cookiejar 的自动维护行为。
    /// </summary>
    public void AbsorbCookiesFromResponse(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("Set-Cookie", out var values))
        {
            foreach (var setCookie in values)
            {
                // 只取 "name=value"，忽略 Path/Expires/Domain 等属性
                var firstPart = setCookie.Split(';')[0].Trim();
                var idx = firstPart.IndexOf('=');
                if (idx <= 0)
                {
                    continue;
                }

                var name = firstPart[..idx].Trim();
                var value = firstPart[(idx + 1)..].Trim();
                SetCookie(name, value);
            }
        }
    }

    public string? GetCookie(string name) =>
        _cookies.FirstOrDefault(c => string.Equals(c.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    public string BuildCookieHeader() =>
        string.Join("; ", _cookies.Select(c => $"{c.Key}={c.Value}"));
}
