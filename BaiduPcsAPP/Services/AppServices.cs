using BaiduPcs.Core.Api;
using BaiduPcs.Core.Auth;
using BaiduPcs.Core.Infrastructure;
using BaiduPcs.Core.Models;

namespace BaiduPcsAPP.Services;

/// <summary>
/// 应用组合根：创建并共享 Core 层核心对象。
/// 不引入第三方 DI 容器，保持简单直观。
/// </summary>
public sealed class AppServices
{
    public AppServices()
    {
        Http = new BaiduHttpClient(new BaiduHttpOptions
        {
            MaxConnectionsPerServer = 32,
            Timeout = TimeSpan.FromSeconds(50),
        });
        Store = new AccountStore();
        Login = new LoginService(Http, Store);
    }

    public BaiduHttpClient Http { get; }

    public AccountStore Store { get; }

    public LoginService Login { get; }

    /// <summary>为当前账号创建 API 客户端。</summary>
    public BaiduPcsApiClient CreateApiClient(BaiduSession session) =>
        new(Http, session, new BaiduPcsApiOptions());

    /// <summary>为当前账号创建 API 客户端。</summary>
    public BaiduPcsApiClient CreateApiClient(BaiduAccount account) =>
        CreateApiClient(account.ToSession());
}
