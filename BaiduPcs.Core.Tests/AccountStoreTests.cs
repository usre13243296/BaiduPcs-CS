using System.Net;
using System.Text;
using BaiduPcs.Core.Api;
using BaiduPcs.Core.Auth;
using BaiduPcs.Core.Infrastructure;
using BaiduPcs.Core.Models;

namespace BaiduPcs.Core.Tests;

public class AccountStoreTests : IDisposable
{
    private readonly string _tempFile;

    public AccountStoreTests()
    {
        _tempFile = Path.Combine(Path.GetTempPath(), $"baidupcs_test_{Guid.NewGuid():N}.json");
    }

    public void Dispose()
    {
        if (File.Exists(_tempFile))
        {
            File.Delete(_tempFile);
        }
    }

    [Fact]
    public void Upsert_PersistsAndRestores()
    {
        var store = new AccountStore(_tempFile);
        store.Upsert(new BaiduAccount { Uid = 100, Name = "测试", Bduss = "bduss-1" });

        var reloaded = new AccountStore(_tempFile);
        Assert.Single(reloaded.Accounts);
        Assert.Equal("测试", reloaded.Accounts[0].Name);
        Assert.Equal(100, reloaded.ActiveUid);
    }

    [Fact]
    public void Upsert_SameUid_Updates()
    {
        var store = new AccountStore(_tempFile);
        store.Upsert(new BaiduAccount { Uid = 100, Name = "A", Bduss = "b1" });
        store.Upsert(new BaiduAccount { Uid = 100, Name = "B", Bduss = "b2" });

        Assert.Single(store.Accounts);
        Assert.Equal("B", store.Accounts[0].Name);
    }

    [Fact]
    public void Remove_And_SetActive()
    {
        var store = new AccountStore(_tempFile);
        store.Upsert(new BaiduAccount { Uid = 1, Name = "A" });
        store.Upsert(new BaiduAccount { Uid = 2, Name = "B" });

        store.SetActive(2);
        Assert.Equal(2, store.ActiveUid);

        store.Remove(2);
        Assert.Equal(1, store.ActiveUid);
        Assert.Single(store.Accounts);
    }

    [Fact]
    public void CorruptFile_IsIgnored()
    {
        File.WriteAllText(_tempFile, "{not valid json");
        var store = new AccountStore(_tempFile);
        Assert.Empty(store.Accounts);
        Assert.Null(store.ActiveUid);
    }

    [Fact]
    public void Account_ToSession_ContainsCookies()
    {
        var account = new BaiduAccount
        {
            Uid = 42,
            Name = "N",
            Bduss = "b",
            Stoken = "s",
            Cookies = "PTOKEN=p1; SBOXTKN=sb",
        };

        var session = account.ToSession();
        Assert.Equal("b", session.Bduss);
        Assert.Equal("s", session.GetCookie("STOKEN"));
        Assert.Equal("p1", session.GetCookie("PTOKEN"));
        Assert.Equal("sb", session.GetCookie("SBOXTKN"));
        Assert.Equal(42, session.Uid);
    }
}

public class LoginServiceTests
{
    private sealed class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public MockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        public List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(_responder(request));
        }
    }

    private static (LoginService Service, MockHttpMessageHandler Handler) CreateLoginService(
        Func<HttpRequestMessage, HttpResponseMessage> responder,
        string storePath)
    {
        var handler = new MockHttpMessageHandler(responder);
        var http = new BaiduHttpClient(options: null, handler: handler);
        return (new LoginService(http, new AccountStore(storePath)), handler);
    }

    private static string NewStorePath() =>
        Path.Combine(Path.GetTempPath(), $"baidupcs_test_{Guid.NewGuid():N}.json");

    private static string HomePageHtml(string uname = "testuser")
    {
        var json = "{\"sign1\":\"e8c7d729eea7b54551aa594f942decbe\",\"sign3\":\"37dbe07ade9359c1aa70807e847f768c13360ad2\",\"timestamp\":1700000000,\"uname\":\"" + uname + "\"}";
        return "<html><body><script>window.__INITIAL_STATE__ = " + json + "</script></body></html>";
    }

    private static HttpResponseMessage Ok(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    [Fact]
    public async Task LoginByBdussAsync_Valid_ReturnsAccount()
    {
        var storePath = NewStorePath();
        try
        {
            var (service, handler) = CreateLoginService(request =>
            {
                if (request.RequestUri!.AbsolutePath == "/api/user/getinfo")
                {
                    return Ok("""{"errno":0,"records":[{"uk":12345}]}""");
                }

                if (request.RequestUri!.AbsolutePath == "/disk/home")
                {
                    // 模拟首页响应下发 cookie（如 BAIDUID），登录时先完成 cookie 初始化
                    var resp = Ok(HomePageHtml("小明"));
                    resp.Headers.TryAddWithoutValidation("Set-Cookie", "BAIDUID=home-baiduid; Path=/");
                    return resp;
                }

                return Ok("{}");
            }, storePath);

            var account = await service.LoginByBdussAsync("bduss-value");

            Assert.Equal(12345, account.Uid);
            Assert.Equal("小明", account.Name);
            Assert.Equal("bduss-value", account.Bduss);

            // 运行时吸收的 cookie 应被合并回账号并持久化
            Assert.Contains("BAIDUID=home-baiduid", account.Cookies);

            // 已持久化并设为当前账号，且持久化的账号也包含运行时 cookie
            var store = new AccountStore(storePath);
            Assert.Single(store.Accounts);
            Assert.Equal(12345, store.ActiveUid);
            Assert.Contains("home-baiduid", store.Accounts[0].Cookies);

            // 重建会话后 cookie 上下文完整（BDUSS + BAIDUID），避免再次被风控拒绝
            var restored = store.Accounts[0].ToSession();
            Assert.Equal("bduss-value", restored.Bduss);
            Assert.Equal("home-baiduid", restored.GetCookie("BAIDUID"));

            // 关键：先访问首页初始化 cookie，再调用 getinfo 获取 UK
            Assert.Equal(2, handler.Requests.Count);
            Assert.Equal("/disk/home", handler.Requests[0].RequestUri!.AbsolutePath);
            Assert.Equal("/api/user/getinfo", handler.Requests[1].RequestUri!.AbsolutePath);

            // 首页响应下发的 cookie 应被吸收，且 getinfo 请求携带该 cookie
            var getinfoRequest = handler.Requests[1];
            Assert.Contains("home-baiduid", getinfoRequest.Headers.GetValues("Cookie").First());
        }
        finally
        {
            if (File.Exists(storePath))
            {
                File.Delete(storePath);
            }
        }
    }

    [Fact]
    public async Task LoginByBdussAsync_InvalidCookie_Throws()
    {
        var storePath = NewStorePath();
        try
        {
            // 首页访问可能成功（下发 cookie），但 getinfo 仍可能因 BDUSS 无效而失败
            var (service, _) = CreateLoginService(request =>
            {
                if (request.RequestUri!.AbsolutePath == "/disk/home")
                {
                    return Ok(HomePageHtml());
                }

                return Ok("""{"errno":-6,"msg":"invalid"}""");
            }, storePath);

            var ex = await Assert.ThrowsAsync<BaiduApiException>(() => service.LoginByBdussAsync("bad-bduss"));
            Assert.Equal(ApiErrorType.RemoteError, ex.ErrorType);
            Assert.Equal(-6, ex.RemoteCode);
        }
        finally
        {
            if (File.Exists(storePath))
            {
                File.Delete(storePath);
            }
        }
    }

    [Fact]
    public async Task LoginByBdussAsync_HomePageRedirectToPassport_FallsBackToUid()
    {
        var storePath = NewStorePath();
        try
        {
            var (service, _) = CreateLoginService(request =>
            {
                if (request.RequestUri!.AbsolutePath == "/api/user/getinfo")
                {
                    return Ok("""{"errno":0,"records":[{"uk":999}]}""");
                }

                if (request.RequestUri!.AbsolutePath == "/disk/home")
                {
                    // 模拟被重定向到 passport（无效 cookie 场景）
                    var resp = new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("<html>login</html>", Encoding.UTF8),
                    };
                    resp.RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://passport.baidu.com/v2/?login");
                    return resp;
                }

                return Ok("{}");
            }, storePath);

            var account = await service.LoginByBdussAsync("bduss-value");
            Assert.Equal(999, account.Uid);
            Assert.Equal("用户999", account.Name);
        }
        finally
        {
            if (File.Exists(storePath))
            {
                File.Delete(storePath);
            }
        }
    }
}
