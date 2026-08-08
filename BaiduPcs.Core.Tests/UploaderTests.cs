using System.Net;
using System.Text;
using BaiduPcs.Core.Api;
using BaiduPcs.Core.Auth;
using BaiduPcs.Core.Infrastructure;
using BaiduPcs.Core.Upload;

namespace BaiduPcs.Core.Tests;

/// <summary>按 URL path 分发的假百度上传服务器（precreate/superfile2/create/meta/locateupload/getinfo）。</summary>
public sealed class FakeUploadServer : HttpMessageHandler
{
    private readonly object _lock = new();

    public record RecordedRequest(string Path, string Query, string Body, byte[] BodyRaw);

    public List<RecordedRequest> Requests { get; } = new();

    public Func<string> PrecreateResponder { get; set; } = () => """{"errno":0,"return_type":1,"uploadid":"up-1"}""";
    public Func<int, string> TmpFileResponder { get; set; } = _ => """{"error_code":0,"md5":"server-md5"}""";
    public string CreateResponder { get; set; } = """{"errno":0}""";
    public string UkResponder { get; set; } = """{"errno":0,"records":[{"uk":777}]}""";
    public string LocateResponder { get; set; } = """{"error_code":0,"server":[]}""";
    public Func<string> MetaResponder { get; set; } = () => """{"error_code":0,"list":[]}""";
    public TimeSpan TmpFileDelay { get; set; } = TimeSpan.Zero;

    /// <summary>设置后 superfile2 请求返回该状态码（配合 TmpFileResponder 的 body）。</summary>
    public HttpStatusCode? TmpFileStatus { get; set; }

    /// <summary>每个请求到达时回调（测试挂起点）。</summary>
    public Action<RecordedRequest>? OnRequest { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        var query = request.RequestUri.Query;
        var raw = request.Content is null
            ? Array.Empty<byte>()
            : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        var record = new RecordedRequest(path, query, Encoding.UTF8.GetString(raw), raw);

        OnRequest?.Invoke(record);
        lock (_lock)
        {
            Requests.Add(record);
        }

        string json;
        if (path.EndsWith("/api/precreate"))
        {
            json = PrecreateResponder();
        }
        else if (path.EndsWith("/api/create"))
        {
            json = CreateResponder;
        }
        else if (path.EndsWith("/api/user/getinfo"))
        {
            json = UkResponder;
        }
        else if (query.Contains("method=locateupload"))
        {
            json = LocateResponder;
        }
        else if (path.EndsWith("/rest/2.0/pcs/superfile2"))
        {
            if (TmpFileDelay > TimeSpan.Zero)
            {
                await Task.Delay(TmpFileDelay, cancellationToken).ConfigureAwait(false);
            }

            json = TmpFileResponder(ParsePartSeq(query));
        }
        else if (query.Contains("method=meta"))
        {
            json = MetaResponder();
        }
        else
        {
            json = """{"error_code":0}""";
        }

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        if (TmpFileStatus is { } status && path.EndsWith("/rest/2.0/pcs/superfile2"))
        {
            response.StatusCode = status;
        }

        return response;
    }

    private static int ParsePartSeq(string query)
    {
        const string marker = "partseq=";
        var idx = query.IndexOf(marker, StringComparison.Ordinal);
        return idx < 0 ? -1 : int.Parse(query[(idx + marker.Length)..].Split('&')[0]);
    }

    public int Count(string path) => Requests.Count(r => r.Path.EndsWith(path));

    public List<RecordedRequest> ByPath(string path) => Requests.Where(r => r.Path.EndsWith(path)).ToList();

    /// <summary>从 query 中解析 key= 参数值。</summary>
    public static long ParseQueryLong(string query, string key)
    {
        var marker = key + "=";
        var idx = query.IndexOf(marker, StringComparison.Ordinal);
        return idx < 0 ? -1 : long.Parse(query[(idx + marker.Length)..].Split('&')[0]);
    }
}

public class UploaderTests : IDisposable
{
    private readonly FakeUploadServer _server;
    private readonly BaiduPcsApiClient _client;
    private readonly string _tempDir;

    public UploaderTests()
    {
        _server = new FakeUploadServer();
        var http = new BaiduHttpClient(options: null, handler: _server);
        var session = BaiduSession.FromBduss("fake-bduss");
        _client = new BaiduPcsApiClient(http, session);
        _tempDir = Path.Combine(Path.GetTempPath(), "upt_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string MakeFile(long size)
    {
        var path = Path.Combine(_tempDir, "data.bin");
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        var buffer = new byte[1 << 20];
        var remaining = size;
        var offset = 0L;
        while (remaining > 0)
        {
            var take = (int)Math.Min(buffer.Length, remaining);
            for (var i = 0; i < take; i++)
            {
                buffer[i] = (byte)(((offset + i) * 31 + (offset + i) / 251) % 251);
            }

            fs.Write(buffer, 0, take);
            offset += take;
            remaining -= take;
        }

        return path;
    }

    private Uploader CreateUploader(string localPath, string remoteDir = "/dir", string? fileName = null, int maxParallel = 4, int maxChunkRetries = 8, UploadSession? session = null)
        => new(new UploaderOptions
        {
            Api = _client,
            LocalFilePath = localPath,
            RemoteDir = remoteDir,
            FileName = fileName,
            MaxParallel = maxParallel,
            MaxChunkRetries = maxChunkRetries,
        }, session ?? new UploadSession());

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
            {
                throw new TimeoutException("等待条件超时");
            }

            await Task.Delay(20);
        }
    }

    /// <summary>从 multipart body 原始字节中提取 uploadedfile 字段的数据部分（header/footer 之间）。</summary>
    private static byte[] ExtractChunkData(byte[] body)
    {
        var marker = Encoding.ASCII.GetBytes("filename=\"\"\r\n\r\n");
        var start = IndexOf(body, marker);
        Assert.True(start >= 0, "multipart 中未找到 uploadedfile 数据起点");
        start += marker.Length;

        var footer = Encoding.ASCII.GetBytes("\r\n--");
        var end = LastIndexOf(body, footer);
        Assert.True(end > start, "multipart 中未找到数据终点");

        return body[start..end];
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return i;
            }
        }

        return -1;
    }

    private static int LastIndexOf(byte[] haystack, byte[] needle)
    {
        for (var i = haystack.Length - needle.Length; i >= 0; i--)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return i;
            }
        }

        return -1;
    }

    // ======================== 秒传 ========================

    [Fact]
    public async Task UploadAsync_RapidUploadHit_NoChunkRequests()
    {
        _server.PrecreateResponder = () => """{"errno":0,"return_type":2,"uploadid":""}""";
        var file = MakeFile(1024);
        var progress = new List<UploadProgress>();

        var uploader = CreateUploader(file);
        await uploader.UploadAsync(new Progress<UploadProgress>(progress.Add));

        Assert.Empty(_server.ByPath("/rest/2.0/pcs/superfile2"));
        Assert.Empty(_server.ByPath("/api/create"));
        Assert.Single(_server.ByPath("/api/precreate"));
        // 秒传完成：进度 100%
        Assert.True(progress.Count > 0);
        Assert.Equal(1024, progress[^1].UploadedBytes);
        Assert.Equal(100, progress[^1].Percent, 2);
        // 秒传进度瞬间读满：所有非零进度值都是 1024（无渐进中间值，与真实上传区分）
        Assert.All(progress.Where(p => p.UploadedBytes > 0), p => Assert.Equal(1024, p.UploadedBytes));
        // 引擎标记秒传命中
        Assert.True(uploader.WasRapidUpload);
        // 秒传是本地瞬间完成，不计网络速度：速度不得虚高（旧实现会报 xxxMB/s 并冻结）
        Assert.All(progress, p => Assert.True(p.SpeedBytesPerSecond < 1024L * 1024 * 1024, $"速度虚高: {p.SpeedBytesPerSecond} B/s"));
    }

    [Fact]
    public async Task UploadAsync_NormalUpload_WasRapidUploadFalse()
    {
        var file = MakeFile(3 * 1024 * 1024);

        var uploader = CreateUploader(file);
        await uploader.UploadAsync();

        // 真实上传：非秒传标记
        Assert.False(uploader.WasRapidUpload);
        Assert.Single(_server.ByPath("/rest/2.0/pcs/superfile2"));
    }

    [Fact]
    public async Task UploadAsync_RapidUploadHit_SendsAllChecksums()
    {
        _server.PrecreateResponder = () => """{"errno":0,"return_type":2,"uploadid":""}""";
        var file = MakeFile(300 * 1024);

        await CreateUploader(file, fileName: "report.txt").UploadAsync();

        var body = _server.ByPath("/api/precreate").Single().Body;
        Assert.Contains("path=%2Fdir%2Freport.txt", body);
        Assert.Contains("target_path=%2Fdir%2F", body);
        Assert.Contains("size=307200", body);
        Assert.Contains("isdir=0", body);
        Assert.Contains("autoinit=1", body);
        Assert.Contains("mode=1", body);
        Assert.Contains("rtype=3", body);
        Assert.Contains("content-md5=", body);
        Assert.Contains("slice-md5=", body);
        Assert.Contains("data_length=4096", body);
        Assert.Contains("data_content=", body);
        Assert.Contains("data_offset=", body);
        Assert.Contains("block_list=%5B%22", body);
    }

    // ======================== 分片上传 ========================

    [Fact]
    public async Task UploadAsync_SingleBlock_UploadsAndCreates()
    {
        var file = MakeFile(3 * 1024 * 1024);
        var progress = new List<UploadProgress>();

        await CreateUploader(file).UploadAsync(new Progress<UploadProgress>(progress.Add));

        // 1 块：1 个 superfile2 请求
        var chunks = _server.ByPath("/rest/2.0/pcs/superfile2");
        Assert.Single(chunks);
        Assert.Contains("partseq=0", chunks[0].Query);
        Assert.Contains("partoffset=0", chunks[0].Query);
        Assert.Contains("uploadid=up-1", chunks[0].Query);
        Assert.Contains("vip=1", chunks[0].Query);
        Assert.Contains("type=tmpfile", chunks[0].Query);
        // multipart 字段名与内容
        Assert.Contains("name=\"uploadedfile\"", chunks[0].Body);
        Assert.Contains("filename=\"\"", chunks[0].Body);
        // 分片数据只含本块字节（限长子流，防误发文件剩余部分）且与本地文件一致
        var chunkData = ExtractChunkData(chunks[0].BodyRaw);
        Assert.Equal(3 * 1024 * 1024, chunkData.Length);
        Assert.Equal(File.ReadAllBytes(file), chunkData);

        // create：block_list 与 precreate 的 md5 一致
        var create = _server.ByPath("/api/create").Single();
        Assert.Contains("uploadid=up-1", create.Body);
        Assert.Contains("target_path=%2Fdir", create.Body);
        Assert.Contains("block_list=%5B%22", create.Body);

        // 进度：插桩粒度平滑报告（多次回调，非 0→100 跳变）、单调递增、最终 100%
        Assert.NotEmpty(progress);
        Assert.True(progress.Count >= 5, $"进度应平滑报告（读取插桩粒度），实际 {progress.Count} 次回调");
        Assert.True(progress.SequenceEqual(progress.OrderBy(p => p.UploadedBytes)));
        Assert.Equal(3 * 1024 * 1024, progress[^1].UploadedBytes);
        Assert.Equal(100, progress[^1].Percent, 2);
    }

    [Fact]
    public async Task UploadAsync_MultiBlock_AllPartSeqsInOrder()
    {
        var file = MakeFile(9 * 1024 * 1024); // 3 块（4MB/4MB/1MB）

        await CreateUploader(file).UploadAsync();

        var chunks = _server.ByPath("/rest/2.0/pcs/superfile2");
        Assert.Equal(3, chunks.Count);
        // partseq → partoffset 一一对应（请求并发到达，按 seq 排序断言）
        var pairs = chunks
            .Select(c => (Seq: FakeUploadServer.ParseQueryLong(c.Query, "partseq"), Offset: FakeUploadServer.ParseQueryLong(c.Query, "partoffset")))
            .OrderBy(p => p.Seq)
            .ToList();
        Assert.Equal(new[] { (0L, 0L), (1L, 4194304L), (2L, 8388608L) }, pairs);

        // 每片数据只含本块字节（限长子流：4MB/4MB/1MB）且与文件对应偏移一致
        var fileBytes = File.ReadAllBytes(file);
        foreach (var chunk in chunks)
        {
            var seq = (int)FakeUploadServer.ParseQueryLong(chunk.Query, "partseq");
            var data = ExtractChunkData(chunk.BodyRaw);
            var offset = seq * 4 * 1024 * 1024;
            var length = Math.Min(4 * 1024 * 1024, fileBytes.Length - offset);
            Assert.Equal(length, data.Length);
            Assert.Equal(fileBytes[offset..(offset + length)], data);
        }
    }

    [Fact]
    public async Task UploadAsync_ChunkFailure_RetriesThenSucceeds()
    {
        var file = MakeFile(9 * 1024 * 1024);
        var attempts = new Dictionary<int, int>();
        _server.TmpFileResponder = seq =>
        {
            attempts.TryGetValue(seq, out var n);
            attempts[seq] = n + 1;
            return seq == 0 && n == 0
                ? """{"error_code":0}""" // 第一次缺 md5（协议层报"未返回 md5"）→ 重试
                : """{"error_code":0,"md5":"server-md5"}""";
        };
        var progress = new List<UploadProgress>();

        await CreateUploader(file).UploadAsync(new Progress<UploadProgress>(progress.Add));

        // 块 0 请求 2 次（失败 1 次 + 成功 1 次），其余各 1 次 → 共 4 次
        Assert.Equal(2, attempts[0]);
        Assert.Equal(4, _server.Count("/rest/2.0/pcs/superfile2"));
        Assert.Single(_server.ByPath("/api/create"));
        // 重试期间进度依然可见（不再静默黑屏）：回调次数充足且最终 100%
        Assert.True(progress.Count >= 5, $"重试场景进度回调过少: {progress.Count}");
        Assert.Equal(100, progress[^1].Percent, 2);
    }

    [Fact]
    public async Task UploadAsync_ChunkFatalHttpStatus_ThrowsFatal()
    {
        var file = MakeFile(3 * 1024 * 1024); // 单块
        _server.TmpFileStatus = HttpStatusCode.Forbidden;

        var ex = await Assert.ThrowsAsync<UploadException>(() => CreateUploader(file).UploadAsync());

        // HTTP 403 → 致命，不重试
        Assert.Equal(UploadErrorKind.Fatal, ex.Kind);
        Assert.Equal(1, _server.Count("/rest/2.0/pcs/superfile2"));
    }

    // ======================== 断点续传 / 会话 ========================

    [Fact]
    public async Task UploadAsync_SessionExpired_RestartsWithFreshSession()
    {
        var file = MakeFile(9 * 1024 * 1024);
        var failOnce = new[] { true };
        _server.TmpFileResponder = seq =>
        {
            if (seq == 1 && failOnce[0])
            {
                failOnce[0] = false;
                return """{"error_code":31363,"error_msg":"block miss in superfile2"}""";
            }

            return """{"error_code":0,"md5":"server-md5"}""";
        };
        // 串行（MaxParallel=1）：3 块任务全部已派发（排队等槽）。块 0 成功 → 块 1 会话失效抛
        // （其 finally 释放槽后块 2 仍会完成上传，round 1 = 3 个请求）→ 第二轮整体重传（3 块）
        var uploader = CreateUploader(file, maxParallel: 1, maxChunkRetries: 8);

        await uploader.UploadAsync();

        // 会话失效后自动重来：precreate 2 次（首次 + 重来）；superfile2 = round1 3 + round2 3 = 6
        Assert.Equal(2, _server.Count("/api/precreate"));
        Assert.Equal(6, _server.Count("/rest/2.0/pcs/superfile2"));
        Assert.Single(_server.ByPath("/api/create"));
    }

    [Fact]
    public async Task UploadAsync_ResumeWithSession_SkipsUploadedBlocks()
    {
        var file = MakeFile(9 * 1024 * 1024); // 3 块
        var session = new UploadSession();
        var failSeq0 = true;

        // 第一轮：块 0 永久失败（MaxChunkRetries=1 快速失败），块 1/2 成功
        _server.TmpFileResponder = seq =>
        {
            if (seq == 0 && failSeq0)
            {
                return """{"error_code":0}"""; // 缺 md5 → 协议层报错
            }

            return """{"error_code":0,"md5":"server-md5"}""";
        };

        var uploader1 = CreateUploader(file, maxChunkRetries: 1, session: session);
        await Assert.ThrowsAsync<UploadException>(() => uploader1.UploadAsync());

        // 会话已建立：uploadid + 块 1/2 已传
        Assert.Equal("up-1", session.UploadId);
        Assert.True(session.UploadedBlocks.ContainsKey(1));
        Assert.True(session.UploadedBlocks.ContainsKey(2));
        Assert.False(session.UploadedBlocks.ContainsKey(0));

        // 第二轮：恢复成功，只传块 0
        var before = _server.Count("/rest/2.0/pcs/superfile2");
        failSeq0 = false;
        var progress = new List<UploadProgress>();
        var uploader2 = CreateUploader(file, maxChunkRetries: 1, session: session);
        await uploader2.UploadAsync(new Progress<UploadProgress>(progress.Add));

        var resumed = _server.ByPath("/rest/2.0/pcs/superfile2").Skip(before).ToList();
        Assert.Single(resumed);
        Assert.Contains("partseq=0", resumed[0].Query);
        // 第二轮 precreate 带原 uploadid 续传
        Assert.Contains("uploadid=up-1", _server.ByPath("/api/precreate")[^1].Body);
        Assert.Single(_server.ByPath("/api/create"));
        // 跳过已传分片（8MB）不计入网络速度：不得虚高（旧实现会报 xxxMB/s 并冻结）
        Assert.All(progress, p => Assert.True(p.SpeedBytesPerSecond < 1024L * 1024 * 1024, $"跳过块导致速度虚高: {p.SpeedBytesPerSecond} B/s"));
    }

    // ======================== 暂停 / 恢复 / 取消 ========================

    [Fact]
    public async Task UploadAsync_Pause_StopsRequests_Resume_Continues()
    {
        var file = MakeFile(9 * 1024 * 1024); // 3 块，串行（MaxParallel=1）+ 每片 100ms 响应
        _server.TmpFileDelay = TimeSpan.FromMilliseconds(100);
        var firstChunkArrived = new TaskCompletionSource();
        _server.OnRequest = r =>
        {
            if (r.Path.EndsWith("/rest/2.0/pcs/superfile2"))
            {
                firstChunkArrived.TrySetResult();
            }
        };
        var uploader = CreateUploader(file, maxParallel: 1);
        var uploadTask = uploader.UploadAsync();

        // 片 0 请求到达后立即暂停：片 0 还有 100ms 未完成，片 1 必然在暂停后挂起
        await firstChunkArrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await uploader.PauseAsync();
        await Task.Delay(300); // 等片 0 完成 + 片 1 挂起

        Assert.Equal(1, _server.Count("/rest/2.0/pcs/superfile2")); // 暂停后无新请求

        await uploader.ResumeAsync();
        await uploadTask.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(3, _server.Count("/rest/2.0/pcs/superfile2"));
        Assert.Single(_server.ByPath("/api/create"));
    }

    [Fact]
    public async Task UploadAsync_Cancel_ThrowsOperationCanceled()
    {
        var file = MakeFile(9 * 1024 * 1024);
        _server.TmpFileDelay = TimeSpan.FromMilliseconds(5000);
        var firstChunkArrived = new TaskCompletionSource();
        _server.OnRequest = r =>
        {
            if (r.Path.EndsWith("/rest/2.0/pcs/superfile2"))
            {
                firstChunkArrived.TrySetResult();
            }
        };
        var uploader = CreateUploader(file);
        var cts = new CancellationTokenSource();
        var uploadTask = uploader.UploadAsync(ct: cts.Token);

        await firstChunkArrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => uploadTask);
    }

    // ======================== 重名与本地校验 ========================

    [Fact]
    public async Task UploadAsync_TargetExists_RenamesWithIncrement()
    {
        var file = MakeFile(1024);
        var existsCount = 0;
        _server.MetaResponder = () =>
        {
            existsCount++;
            return existsCount <= 2
                ? """{"error_code":0,"list":[{"fs_id":1,"path":"/dir/report.txt","server_filename":"report.txt","isdir":0}]}"""
                : """{"error_code":0,"list":[]}""";
        };

        await CreateUploader(file, fileName: "report.txt").UploadAsync();

        // report.txt、report2.txt 均存在 → 上传为 report3.txt
        var body = _server.ByPath("/api/precreate").Single().Body;
        Assert.Contains("path=%2Fdir%2Freport3.txt", body);
        Assert.Contains("target_path=%2Fdir%2F", body);
        Assert.Equal(3, existsCount);
    }

    [Fact]
    public async Task UploadAsync_TargetFree_UsesOriginalName()
    {
        var file = MakeFile(1024);

        await CreateUploader(file, fileName: "report.txt").UploadAsync();

        Assert.Contains("path=%2Fdir%2Freport.txt", _server.ByPath("/api/precreate").Single().Body);
    }

    [Fact]
    public async Task UploadAsync_LocalFileMissing_ThrowsLocalFileError()
    {
        var missing = Path.Combine(_tempDir, "missing.bin");

        var ex = await Assert.ThrowsAsync<UploadException>(() => CreateUploader(missing).UploadAsync());

        Assert.Equal(UploadErrorKind.LocalFileError, ex.Kind);
    }

    [Fact]
    public async Task UploadAsync_EmptyFile_CompletesViaPrecreate()
    {
        var file = MakeFile(0);

        await CreateUploader(file).UploadAsync();

        // 空文件：无分片请求，直接 create
        Assert.Empty(_server.ByPath("/rest/2.0/pcs/superfile2"));
        Assert.Single(_server.ByPath("/api/create"));
    }
}
