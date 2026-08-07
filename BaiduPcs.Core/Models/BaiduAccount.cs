using BaiduPcs.Core.Auth;

namespace BaiduPcs.Core.Models;

/// <summary>
/// 百度账号（可持久化）。对应 Go 端 pcsconfig.Baidu。
/// </summary>
public sealed class BaiduAccount
{
    /// <summary>百度 uid</summary>
    public long Uid { get; set; }

    /// <summary>用户名（昵称）</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>BDUSS（登录凭据核心）</summary>
    public string Bduss { get; set; } = string.Empty;

    public string Ptoken { get; set; } = string.Empty;
    public string Stoken { get; set; } = string.Empty;
    public string Sboxtkn { get; set; } = string.Empty;
    public string Baiduid { get; set; } = string.Empty;

    /// <summary>完整 cookie 串（高级模式粘贴），可为空</summary>
    public string Cookies { get; set; } = string.Empty;

    /// <summary>默认网盘工作目录</summary>
    public string Workdir { get; set; } = "/";

    /// <summary>最近一次登录/使用时间</summary>
    public DateTime LastLoginAt { get; set; } = DateTime.UtcNow;

    /// <summary>构建会话对象（用于创建 API 客户端）。</summary>
    public BaiduSession ToSession()
    {
        var session = BaiduSession.FromBduss(Bduss);
        if (!string.IsNullOrEmpty(Stoken))
        {
            session.SetCookie("STOKEN", Stoken);
        }

        if (!string.IsNullOrEmpty(Ptoken))
        {
            session.SetCookie("PTOKEN", Ptoken);
        }

        if (!string.IsNullOrEmpty(Sboxtkn))
        {
            session.SetCookie("SBOXTKN", Sboxtkn);
        }

        if (!string.IsNullOrEmpty(Baiduid))
        {
            session.SetCookie("BAIDUID", Baiduid);
        }

        if (!string.IsNullOrEmpty(Cookies))
        {
            session.SetCookiesFromString(Cookies);
        }

        session.Uid = Uid;
        session.Name = Name;
        session.Workdir = string.IsNullOrEmpty(Workdir) ? "/" : Workdir;
        return session;
    }
}
