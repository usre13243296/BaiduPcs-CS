namespace BaiduPcs.Core.Auth;

/// <summary>Cookie 解析工具（对应 Go 端 requester.ParseCookieStr）。</summary>
public static class CookieParser
{
    /// <summary>
    /// 解析 Cookie 字符串为 (name, value) 列表。
    /// 只接受 "name=value" 形式，跳过无法解析的分段。
    /// </summary>
    public static List<KeyValuePair<string, string>> Parse(string cookieString)
    {
        var result = new List<KeyValuePair<string, string>>();
        if (string.IsNullOrEmpty(cookieString))
        {
            return result;
        }

        foreach (var raw in cookieString.Split(';'))
        {
            var segments = raw.Split('=', 2);
            if (segments.Length < 2)
            {
                continue;
            }

            var name = segments[0].Trim();
            var value = segments[1].Trim();
            if (name.Length == 0)
            {
                continue;
            }

            result.Add(new KeyValuePair<string, string>(name, value));
        }

        return result;
    }
}
