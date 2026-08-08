using System.Net;
using System.Text;
using BaiduPcs.Core.Api;
using BaiduPcs.Core.Auth;
using BaiduPcs.Core.Infrastructure;

namespace BaiduPcs.Core.Tests;

public class UploadApiTests
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

    // ======================== precreate ========================

    [Fact]
    public async Task PrecreateAsync_SendsAllParams_AndParsesUploadId()
    {
        var (client, handler) = CreateClient(_ => JsonResponse("""{"errno":0,"return_type":1,"uploadid":"up-123"}"""));
        client.Session.Uid = 777;

        var result = await client.PrecreateAsync(
            targetPath: "/dir/report.txt",
            size: 1024,
            contentMd5: "ABCDEF",
            sliceMd5: "123456",
            blockList: new[] { "md5-1", "md5-2" },
            dataOffset: 42,
            dataContent: "base64content",
            localMtime: 1600000000,
            rtype: 3,
            uploadId: null);

        Assert.False(result.IsRapidUpload);
        Assert.Equal("up-123", result.UploadId);

        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Post, request.Method);
        var url = request.RequestUri!.ToString();
        Assert.Contains("/api/precreate", url);

        // UA 与 cookie 与现有管道一致
        Assert.Contains("netdisk", request.Headers.GetValues("User-Agent").First());
        Assert.Contains("BDUSS=fake-bduss", request.Headers.GetValues("Cookie").First());

        var body = await request.Content!.ReadAsStringAsync();
        Assert.Contains("path=%2Fdir%2Freport.txt", body);
        Assert.Contains("target_path=%2Fdir%2F", body);   // precreate 的 target_path 带尾斜杠
        Assert.Contains("size=1024", body);
        Assert.Contains("data_offset=42", body);
        Assert.Contains("isdir=0", body);
        Assert.Contains("local_mtime=1600000000", body);
        Assert.Contains("local_ctime=1600000000", body);
        Assert.Contains("data_time=1600000000", body);
        Assert.Contains("rtype=3", body);
        Assert.Contains("checkexist=0", body);
        Assert.Contains("autoinit=1", body);
        Assert.Contains("content-md5=abcdef", body);       // md5 强制小写
        Assert.Contains("slice-md5=123456", body);
        Assert.Contains("data_length=4096", body);
        Assert.Contains("data_content=base64content", body);
        Assert.Contains("block_list=%5B%22md5-1%22%2C%22md5-2%22%5D", body); // ["md5-1","md5-2"]
        Assert.Contains("mode=1", body);
        Assert.DoesNotContain("uploadid=", body);         // 首次上传不带 uploadid
    }

    [Fact]
    public async Task PrecreateAsync_RapidUploadHit_ReturnsIsRapidUpload()
    {
        var (client, _) = CreateClient(_ => JsonResponse("""{"errno":0,"return_type":2,"uploadid":""}"""));

        var result = await client.PrecreateAsync(
            targetPath: "/a.txt", size: 10, contentMd5: "a", sliceMd5: "b",
            blockList: new[] { "c" }, dataOffset: 0, dataContent: "",
            localMtime: 1, rtype: 3, uploadId: null);

        Assert.True(result.IsRapidUpload);
        Assert.Null(result.UploadId);
    }

    [Fact]
    public async Task PrecreateAsync_Resume_IncludesUploadId()
    {
        var (client, handler) = CreateClient(_ => JsonResponse("""{"errno":0,"return_type":1,"uploadid":"up-9"}"""));

        await client.PrecreateAsync(
            targetPath: "/a.txt", size: 10, contentMd5: "a", sliceMd5: "b",
            blockList: new[] { "c" }, dataOffset: 0, dataContent: "",
            localMtime: 1, rtype: 3, uploadId: "up-9");

        var body = await handler.Requests.Single().Content!.ReadAsStringAsync();
        Assert.Contains("uploadid=up-9", body);
    }

    [Fact]
    public async Task PrecreateAsync_PanErrno_ThrowsRemoteError()
    {
        var (client, _) = CreateClient(_ => JsonResponse("""{"errno":31112}"""));

        var ex = await Assert.ThrowsAsync<BaiduApiException>(() => client.PrecreateAsync(
            targetPath: "/a.txt", size: 10, contentMd5: "a", sliceMd5: "b",
            blockList: new[] { "c" }, dataOffset: 0, dataContent: "",
            localMtime: 1, rtype: 3, uploadId: null));

        Assert.Equal(ApiErrorType.RemoteError, ex.ErrorType);
        Assert.Equal(31112, ex.RemoteCode);
        Assert.Contains("网盘容量已满", ex.RemoteMessage);
    }

    // ======================== superfile2 分片上传 ========================

    [Fact]
    public async Task UploadTmpFileAsync_SendsMultipartWithUploadedFile()
    {
        // 协议层在请求发送后立即释放 multipart（StreamContent 持有上传流），
        // 因此 body 必须在 responder 内捕获
        string? capturedBody = null;
        var (client, handler) = CreateClient(req =>
        {
            capturedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonResponse("""{"error_code":0,"md5":"chunk-md5"}""");
        });

        var md5 = await client.UploadTmpFileAsync(
            pcsHost: "nj01pcs01.pcs.baidu.com",
            targetPath: "/dir/report.txt",
            uploadId: "up-123",
            partSeq: 2,
            partOffset: 8388608,
            content: new MemoryStream(Encoding.UTF8.GetBytes("chunk-data")));

        Assert.Equal("chunk-md5", md5);

        var request = handler.Requests.Single();
        var url = request.RequestUri!.ToString();
        Assert.Contains("nj01pcs01.pcs.baidu.com", url);
        Assert.Contains("/rest/2.0/pcs/superfile2", url);
        Assert.Contains("method=upload", url);
        Assert.Contains("type=tmpfile", url);
        Assert.Contains("path=%2fdir%2freport.txt", url); // URL query 用 HttpUtility 编码（小写 hex）
        Assert.Contains("partseq=2", url);
        Assert.Contains("partoffset=8388608", url);
        Assert.Contains("uploadid=up-123", url);
        Assert.Contains("vip=1", url);

        // multipart：字段名 uploadedfile、filename 空串、内容字节正确
        var body = capturedBody!;
        Assert.Contains("multipart/form-data", request.Content!.Headers.ContentType!.ToString());
        Assert.Contains("name=\"uploadedfile\"", body);
        Assert.Contains("filename=\"\"", body);
        Assert.Contains("chunk-data", body);
    }

    [Fact]
    public async Task UploadTmpFileAsync_UsesDefaultHostWhenEmpty()
    {
        var (client, handler) = CreateClient(_ => JsonResponse("""{"error_code":0,"md5":"m"}"""));

        await client.UploadTmpFileAsync(
            pcsHost: "", targetPath: "/a.txt", uploadId: "up-1",
            partSeq: 0, partOffset: 0, content: new MemoryStream(new byte[1]));

        Assert.Contains("pcs.baidu.com", handler.Requests.Single().RequestUri!.Host);
    }

    [Fact]
    public async Task UploadTmpFileAsync_EmptyMd5_Throws()
    {
        var (client, _) = CreateClient(_ => JsonResponse("""{"error_code":0,"md5":""}"""));

        var ex = await Assert.ThrowsAsync<BaiduApiException>(() => client.UploadTmpFileAsync(
            pcsHost: "", targetPath: "/a.txt", uploadId: "up-1",
            partSeq: 0, partOffset: 0, content: new MemoryStream(new byte[1])));

        Assert.Contains("md5", ex.Message);
    }

    [Fact]
    public async Task UploadTmpFileAsync_PcsErrorCode_Throws()
    {
        var (client, _) = CreateClient(_ => JsonResponse("""{"error_code":31363,"error_msg":"block miss"}"""));

        var ex = await Assert.ThrowsAsync<BaiduApiException>(() => client.UploadTmpFileAsync(
            pcsHost: "", targetPath: "/a.txt", uploadId: "up-1",
            partSeq: 0, partOffset: 0, content: new MemoryStream(new byte[1])));

        Assert.Equal(ApiErrorType.RemoteError, ex.ErrorType);
        Assert.Equal(31363, ex.RemoteCode);
        Assert.Contains("上传状态过期", ex.RemoteMessage);
    }

    // ======================== create 合并 ========================

    [Fact]
    public async Task CreateAsync_SendsParamsInOrder()
    {
        var (client, handler) = CreateClient(_ => JsonResponse("""{"errno":0}"""));

        await client.CreateAsync(
            targetPath: "/dir/report.txt",
            size: 1024,
            uploadId: "up-123",
            blockList: new[] { "md5-1", "md5-2" },
            rtype: 3);

        var request = handler.Requests.Single();
        var url = request.RequestUri!.ToString();
        Assert.Contains("/api/create", url);

        var body = await request.Content!.ReadAsStringAsync();
        Assert.Contains("uploadid=up-123", body);
        Assert.Contains("path=%2Fdir%2Freport.txt", body);
        Assert.Contains("size=1024", body);
        Assert.Contains("isdir=0", body);
        Assert.Contains("rtype=3", body);
        // create 的 target_path 不带尾斜杠
        Assert.Contains("target_path=%2Fdir", body);
        // block_list 按传入顺序（调用方保证 partseq 升序）
        Assert.Contains("block_list=%5B%22md5-1%22%2C%22md5-2%22%5D", body);
    }

    [Fact]
    public async Task CreateAsync_PanErrno_Throws()
    {
        var (client, _) = CreateClient(_ => JsonResponse("""{"errno":31061}"""));

        var ex = await Assert.ThrowsAsync<BaiduApiException>(() => client.CreateAsync(
            targetPath: "/a.txt", size: 10, uploadId: "up-1", blockList: new[] { "m" }, rtype: 3));

        Assert.Equal(ApiErrorType.RemoteError, ex.ErrorType);
        Assert.Equal(31061, ex.RemoteCode);
        Assert.Contains("文件已存在", ex.RemoteMessage);
    }

    // ======================== locateupload ========================

    [Fact]
    public async Task LocateUploadHostsAsync_PrefersServersWithDash()
    {
        var (client, handler) = CreateClient(_ => JsonResponse(
            """{"error_code":0,"host":"pcs.baidu.com","server":["nj01pcs01.pcs.baidu.com","c.pcs.baidu.com"],"servers":[{"server":"https://nj01pcs01-1.pcs.baidu.com"},{"server":"https://c.pcs.baidu.com"}]}"""));

        var hosts = await client.LocateUploadHostsAsync();

        // 只取 servers 中含 '-' 的 hostname；server[] 仅作兜底
        Assert.Equal(new[] { "nj01pcs01-1.pcs.baidu.com" }, hosts);

        var url = handler.Requests.Single().RequestUri!.ToString();
        Assert.Contains("/rest/2.0/pcs/file", url);
        Assert.Contains("method=locateupload", url);
        Assert.Contains("upload_version=2.0", url);
        Assert.Contains("app_id=250528", url);
    }

    [Fact]
    public async Task LocateUploadHostsAsync_FallsBackToServerArray()
    {
        var (client, _) = CreateClient(_ => JsonResponse(
            """{"error_code":0,"servers":[{"server":"https://c.pcs.baidu.com"}],"server":["d.pcs.baidu.com"]}"""));

        var hosts = await client.LocateUploadHostsAsync();

        // servers 无 '-' → 兜底 server[] 全量
        Assert.Equal(new[] { "d.pcs.baidu.com" }, hosts);
    }

    [Fact]
    public async Task LocateUploadHostsAsync_Empty_ReturnsEmptyList()
    {
        var (client, _) = CreateClient(_ => JsonResponse("""{"error_code":0}"""));

        var hosts = await client.LocateUploadHostsAsync();

        Assert.Empty(hosts);
    }
}

public class UploadErrorCatalogTests
{
    [Fact]
    public void FindPcsError_UploadCodes()
    {
        var (_, q) = ErrorMessageCatalog.FindPcsError(31112, "raw");
        Assert.Contains("网盘容量已满", q);

        var (_, s) = ErrorMessageCatalog.FindPcsError(31363, "raw");
        Assert.Contains("上传状态过期", s);

        var (_, f) = ErrorMessageCatalog.FindPcsError(31079, "raw");
        Assert.Contains("秒传文件失败", f);
    }
}
