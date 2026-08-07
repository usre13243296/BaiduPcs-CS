using System.Net;
using System.Text;
using System.Text.Json;
using BaiduPcs.Core.Api;
using BaiduPcs.Core.Auth;
using BaiduPcs.Core.Infrastructure;
using BaiduPcs.Core.Models;

namespace BaiduPcs.Core.Tests;

public class ModelJsonTests
{
    [Fact]
    public void FileEntry_DeserializesFromBaiduJson()
    {
        const string json = """
            {
              "fs_id": 123456,
              "app_id": 250528,
              "path": "/我的资源/1.mp4",
              "server_filename": "1.mp4",
              "ctime": 1600000000,
              "mtime": 1600000001,
              "md5": "0123456789abcdef0123456789abcdef",
              "block_list": ["0123456789abcdef0123456789abcdef"],
              "size": 1024,
              "isdir": 0,
              "ifhassubdir": 0
            }
            """;

        var entry = JsonSerializer.Deserialize<FileEntry>(json)!;

        Assert.Equal(123456, entry.FsId);
        Assert.Equal("/我的资源/1.mp4", entry.Path);
        Assert.Equal("1.mp4", entry.Filename);
        Assert.Equal(1024, entry.Size);
        Assert.False(entry.IsDir);
        Assert.Single(entry.BlockList!);
    }

    [Fact]
    public void FileEntry_DirectoryFlag_Parses()
    {
        const string json = """{"fs_id":1,"path":"/a","server_filename":"a","isdir":1,"ifhassubdir":1}""";
        var entry = JsonSerializer.Deserialize<FileEntry>(json)!;
        Assert.True(entry.IsDir);
        Assert.True(entry.HasSubDir);
    }
}

public class ApiClientPipelineTests
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

    private static (BaiduPcsApiClient Client, MockHttpMessageHandler Handler) CreateClient(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new MockHttpMessageHandler(responder);
        var http = new BaiduHttpClient(options: null, handler: handler);
        var session = BaiduSession.FromBduss("fake-bduss");
        var client = new BaiduPcsApiClient(http, session);
        return (client, handler);
    }

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    [Fact]
    public async Task GetQuotaInfoAsync_ParsesQuota()
    {
        var (client, _) = CreateClient(_ => JsonResponse("""{"error_code":0,"quota":1099511627776,"used":1073741824}"""));

        var quota = await client.GetQuotaInfoAsync();

        Assert.Equal(1099511627776, quota.Quota);
        Assert.Equal(1073741824, quota.Used);
        Assert.Equal(1099511627776 - 1073741824, quota.Free);
    }

    [Fact]
    public async Task GetQuotaInfoAsync_PcsErrorCode_ThrowsRemoteError()
    {
        var (client, _) = CreateClient(_ => JsonResponse("""{"error_code":31066,"error_msg":"file not found"}"""));

        var ex = await Assert.ThrowsAsync<BaiduApiException>(() => client.GetQuotaInfoAsync());
        Assert.Equal(ApiErrorType.RemoteError, ex.ErrorType);
        Assert.Equal(31066, ex.RemoteCode);
        Assert.Contains("文件或目录不存在", ex.RemoteMessage);
    }

    [Fact]
    public async Task GetQuotaInfoAsync_HttpError_Throws()
    {
        var (client, _) = CreateClient(_ =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("""{"error_code":-6,"error_msg":"access denied"}"""),
            };
            return resp;
        });

        var ex = await Assert.ThrowsAsync<BaiduApiException>(() => client.GetQuotaInfoAsync());
        Assert.Equal(ApiErrorType.RemoteError, ex.ErrorType);
    }

    [Fact]
    public async Task GetUkAsync_ThrowsWhenNoRecords()
    {
        var (client, handler) = CreateClient(_ => JsonResponse("""{"errno":0,"records":[]}"""));
        var ex = await Assert.ThrowsAsync<BaiduApiException>(() => client.GetUkAsync());
        Assert.Equal(ApiErrorType.Other, ex.ErrorType);

        // 请求应指向 /api/user/getinfo，携带 clienttype/app_id 参数，并使用 netdisk 客户端 UA
        var request = handler.Requests.Single();
        var url = request.RequestUri!.ToString();
        Assert.Contains("/api/user/getinfo", url);
        Assert.Contains("clienttype=0", url);
        Assert.Contains("app_id=", url);
        Assert.Contains("netdisk", request.Headers.GetValues("User-Agent").First());
    }

    [Fact]
    public async Task GetUkAsync_PanErrno_ThrowsPanError()
    {
        var (client, handler) = CreateClient(_ => JsonResponse("""{"errno":-6}"""));
        var ex = await Assert.ThrowsAsync<BaiduApiException>(() => client.GetUkAsync());
        Assert.Equal(ApiErrorType.RemoteError, ex.ErrorType);
        Assert.Equal(-6, ex.RemoteCode);
        Assert.Contains("请重新登录", ex.RemoteMessage);

        var url = handler.Requests.Single().RequestUri!.ToString();
        Assert.Contains("/api/user/getinfo", url);
        Assert.Contains("need_selfinfo=1", url);
    }

    [Fact]
    public async Task GetUkAsync_AbsorbsSetCookieFromResponse()
    {
        var (client, _) = CreateClient(_ =>
        {
            var resp = JsonResponse("""{"errno":0,"records":[{"uk":777}]}""");
            resp.Headers.TryAddWithoutValidation("Set-Cookie", "BAIDUID=abc123; Path=/; Domain=.baidu.com");
            return resp;
        });

        await client.GetUkAsync();

        // 服务器下发的 cookie 应被吸收进会话，供后续请求自动携带
        Assert.Equal("abc123", client.Session.GetCookie("BAIDUID"));
        Assert.Contains("BAIDUID=abc123", client.Session.BuildCookieHeader());
    }
    [Fact]
    public async Task ListFilesAsync_ParsesList()
    {
        var (client, handler) = CreateClient(_ => JsonResponse(
            """{"error_code":0,"list":[{"fs_id":1,"path":"/a.txt","server_filename":"a.txt","isdir":0,"size":10}]}"""));

        var list = await client.ListFilesAsync("/");

        Assert.Single(list);
        Assert.Equal("a.txt", list[0].Filename);
        // 请求应携带 cookie 与 PCS UA
        var request = handler.Requests.Single();
        Assert.Contains("BDUSS=fake-bduss", request.Headers.GetValues("Cookie").First());
        var url = request.RequestUri!.ToString();
        Assert.Contains("/rest/2.0/pcs/file", url);
        Assert.Contains("method=list", url);
        Assert.Contains("path=%2f", url);
    }

    [Fact]
    public async Task GetMetaAsync_SendsMultipartParam()
    {
        var (client, handler) = CreateClient(_ => JsonResponse("""{"error_code":0,"list":[]}"""));

        await client.GetMetaAsync(new[] { "/a", "/b" });

        var request = handler.Requests.Single();
        var body = await request.Content!.ReadAsStringAsync();
        Assert.Contains("\"path\":\"/a\"", body);
        Assert.Contains("\"path\":\"/b\"", body);
        Assert.Contains("name=\"param\"", body);
    }

    [Fact]
    public async Task MkdirAsync_PostsMethod()
    {
        var (client, handler) = CreateClient(_ => JsonResponse("""{"error_code":0}"""));

        await client.MkdirAsync("/newdir");

        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Contains("method=mkdir", request.RequestUri!.ToString());
    }

    [Fact]
    public async Task RenameAsync_SendsMoveEnvelope()
    {
        var (client, handler) = CreateClient(_ => JsonResponse("""{"error_code":0}"""));

        await client.RenameAsync("/a", "/b");

        var request = handler.Requests.Single();
        var body = await request.Content!.ReadAsStringAsync();
        Assert.Contains("method=move", request.RequestUri!.ToString());
        Assert.Contains("\"from\":\"/a\"", body);
        Assert.Contains("\"to\":\"/b\"", body);
    }
}

public class DownloadLinkTests
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

    private static (BaiduPcsApiClient Client, MockHttpMessageHandler Handler) CreateClient(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new MockHttpMessageHandler(responder);
        var http = new BaiduHttpClient(options: null, handler: handler);
        var session = BaiduSession.FromBduss("fake-bduss");
        var client = new BaiduPcsApiClient(http, session);
        return (client, handler);
    }

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    [Fact]
    public async Task GetDownloadLinksAsync_ReturnsEncryptZeroLinks()
    {
        var (client, handler) = CreateClient(_ => JsonResponse(
            """{"error_code":0,"urls":[{"url":"https://d1.baidu.com/a.bin","encrypt":0},{"url":"https://d2.baidu.com/b.bin","encrypt":0},{"url":"https://d3.baidu.com/c.bin","encrypt":1}]}"""));
        client.Session.Uid = 12345;

        var links = await client.GetDownloadLinksAsync("/1.bin");

        Assert.Equal(2, links.Count);
        Assert.Equal("https://d1.baidu.com/a.bin", links[0]);
        Assert.Equal("https://d2.baidu.com/b.bin", links[1]);

        // 请求应携带 locatedownload 签名参数与 netdisk UA
        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Contains("method=locatedownload", request.RequestUri!.ToString());
        Assert.Contains("path=%2f1.bin", request.RequestUri.ToString());
        Assert.Contains("rand=", request.RequestUri.ToString());
        Assert.Contains("devuid=", request.RequestUri.ToString());
        Assert.Contains("netdisk", request.Headers.GetValues("User-Agent").First());
    }

    [Fact]
    public async Task GetDownloadLinksAsync_SkipsNbCacheHost()
    {
        var (client, _) = CreateClient(_ => JsonResponse(
            """{"error_code":0,"urls":[{"url":"https://nb.cache.baidu.com/x.bin","encrypt":0},{"url":"https://d1.baidu.com/a.bin","encrypt":0}]}"""));
        client.Session.Uid = 12345;

        var links = await client.GetDownloadLinksAsync("/1.bin");

        Assert.Single(links);
        Assert.Equal("https://d1.baidu.com/a.bin", links[0]);
    }

    [Fact]
    public async Task GetDownloadLinksAsync_DlinkListFallback()
    {
        var (client, _) = CreateClient(_ => JsonResponse(
            """{"error_code":0,"dlink":[{"dlink":"https://d1.baidu.com/a.bin","fs_id":"1"}]}"""));
        client.Session.Uid = 12345;

        var links = await client.GetDownloadLinksAsync("/1.bin");

        Assert.Single(links);
        Assert.Equal("https://d1.baidu.com/a.bin", links[0]);
    }

    [Fact]
    public async Task GetDownloadLinksAsync_NoLinks_Throws()
    {
        var (client, _) = CreateClient(_ => JsonResponse("""{"error_code":0,"urls":[]}"""));
        client.Session.Uid = 12345;

        var ex = await Assert.ThrowsAsync<BaiduApiException>(() => client.GetDownloadLinksAsync("/1.bin"));
        Assert.Contains("未找到下载链接", ex.Message);
    }

    [Fact]
    public async Task GetDownloadLinksAsync_MissingUid_Throws()
    {
        var (client, _) = CreateClient(_ => JsonResponse("""{"error_code":0}"""));
        // Uid 未设置（默认 0）

        var ex = await Assert.ThrowsAsync<BaiduApiException>(() => client.GetDownloadLinksAsync("/1.bin"));
        Assert.Contains("UID", ex.Message);
    }

    [Fact]
    public async Task BuildDownloadUrlsAsync_AppendsOfficialFallback()
    {
        var (client, handler) = CreateClient(_ => JsonResponse(
            """{"error_code":0,"urls":[{"url":"https://d1.baidu.com/a.bin","encrypt":0}]}"""));
        client.Session.Uid = 12345;

        var urls = await client.BuildDownloadUrlsAsync("/1.bin");

        // locatedownload 直链在前，method=download 官方通道兜底在后
        Assert.Equal(2, urls.Count);
        Assert.Equal("https://d1.baidu.com/a.bin", urls[0]);
        Assert.Contains("method=download", urls[1]);
        Assert.Contains("path=%2f1.bin", urls[1]);
    }

    [Fact]
    public async Task BuildDownloadUrlsAsync_LocateFails_UsesOfficialOnly()
    {
        var (client, _) = CreateClient(_ => JsonResponse("""{"errno":-6}"""));
        client.Session.Uid = 12345;

        var urls = await client.BuildDownloadUrlsAsync("/1.bin");

        // locatedownload 失败（errno=-6 需要重新登录）时仅保留官方通道兜底
        Assert.Single(urls);
        Assert.Contains("method=download", urls[0]);
    }
}

public class ErrorMessageCatalogTests
{
    [Fact]
    public void FindPanError_KnownCode()
    {
        Assert.Equal("请重新登录", ErrorMessageCatalog.FindPanError(-6));
        Assert.Equal("文件已存在", ErrorMessageCatalog.FindPanError(-30));
    }

    [Fact]
    public void FindPanError_UnknownCode()
    {
        Assert.Equal("未知错误", ErrorMessageCatalog.FindPanError(99999));
    }

    [Fact]
    public void FindPcsError_KnownCode()
    {
        var (code, msg) = ErrorMessageCatalog.FindPcsError(31066, "raw");
        Assert.Equal(31066, code);
        Assert.Contains("文件或目录不存在", msg);
    }
}
