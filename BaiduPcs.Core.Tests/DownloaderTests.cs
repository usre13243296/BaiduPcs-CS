using BaiduPcs.Core.Download;
using BaiduPcs.Core.Tests.Infrastructure;

namespace BaiduPcs.Core.Tests;

/// <summary>下载引擎集成测试：真实 HTTP 走本地 TestHttpServer，覆盖并发/断点/重试/暂停取消/进度。</summary>
public class DownloaderTests : IDisposable
{
    private readonly TestHttpServer _server = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "baidupcs-test-" + Guid.NewGuid().ToString("N"));

    public DownloaderTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        _server.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
        }
    }

    private string PartPath(string name) => Path.Combine(_dir, name + ".part");
    private string CheckpointPath(string name) => Path.Combine(_dir, name + ".part.json");

    /// <summary>确定性内容：偏移相关模式，任何重复/缺口都会破坏比对。</summary>
    private static byte[] MakeContent(long size)
    {
        var bytes = new byte[size];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)((i * 31 + i / 251) % 251);
        }

        return bytes;
    }

    private Downloader CreateDownloader(string path, int maxParallel = 8, TimeSpan? readTimeout = null, string? cookie = "BDUSS=test")
        => new(new DownloaderOptions
        {
            Urls = new[] { _server.BaseUrl + path.TrimStart('/') },
            MaxParallel = maxParallel,
            ReadTimeout = readTimeout ?? TimeSpan.FromSeconds(30),
            CookieHeader = cookie,
        });

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, Func<string>? dump = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > timeout)
            {
                throw new TimeoutException("等待条件超时" + (dump is null ? string.Empty : ": " + dump()));
            }

            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task Download_SimpleFile_ContentMatches()
    {
        var content = MakeContent(100_000);
        _server.AddFile("/simple.bin", content);

        var dl = CreateDownloader("/simple.bin");
        var part = PartPath("simple");
        await dl.DownloadAsync(part, CheckpointPath("simple"));

        Assert.Equal(content, File.ReadAllBytes(part));
        Assert.False(File.Exists(CheckpointPath("simple")), "完成后应删除断点文件");
    }

    [Fact]
    public async Task Download_LargeFile_UsesMultipleRangeConnections()
    {
        var content = MakeContent(2 * 1024 * 1024);
        _server.AddFile("/big.bin", content);

        var dl = CreateDownloader("/big.bin", maxParallel: 8);
        var part = PartPath("big");
        await dl.DownloadAsync(part, CheckpointPath("big"));

        Assert.Equal(content, File.ReadAllBytes(part));

        // 2MB → 256KB 分片：应产生多个不同 Range 的分片请求（多连接并发）
        var ranges = _server.Requests
            .Where(r => r.Path == "/big.bin" && r.Range is not null)
            .Select(r => r.Range)
            .Distinct()
            .ToList();
        Assert.True(ranges.Count >= 3, $"期望多个分片 Range 请求, 实际 {ranges.Count}: {string.Join(", ", ranges)}");
    }

    [Fact]
    public async Task Download_EmptyFile_Completes()
    {
        _server.AddFile("/empty.bin", Array.Empty<byte>());

        var dl = CreateDownloader("/empty.bin");
        var part = PartPath("empty");
        await dl.DownloadAsync(part, CheckpointPath("empty"));

        Assert.Empty(File.ReadAllBytes(part));
    }

    [Fact]
    public async Task Download_ServerIgnoresRange_FallsBackToSingleStream()
    {
        var content = MakeContent(300_000);
        _server.AddFile("/norange.bin", content);
        _server.AddBehavior(async (ctx, srv) =>
        {
            if (ctx.Request.Url!.AbsolutePath != "/norange.bin")
            {
                return false;
            }

            // 忽略 Range，总是返回 200 全量
            var bytes = srv.GetFile("/norange.bin")!;
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
            return true;
        });

        var dl = CreateDownloader("/norange.bin");
        var part = PartPath("norange");
        await dl.DownloadAsync(part, CheckpointPath("norange"));

        Assert.Equal(content, File.ReadAllBytes(part));
        Assert.True(_server.Requests.Count(r => r.Path == "/norange.bin" && r.StatusCode == 200) >= 2);
    }

    [Fact]
    public async Task Download_Cancel_PreservesCheckpoint_ThenResumeCompletes()
    {
        // 4MB → 256KB 块 × 16 块；每响应 800ms 放慢：探测 + 首轮 8 并发 ≈ 1.6s 才到 9 个请求，
        // 保证下载持续 > 1s（断点监控循环 ~1s tick 必然落地）且取消时仍有分片在途
        // （防快机器上瞬时下完导致的竞态：断点未写或取消不生效）
        var content = MakeContent(4 * 1024 * 1024);
        _server.AddFile("/resume.bin", content);
        _server.AddBehavior(async (ctx, _) =>
        {
            await Task.Delay(800);
            return false; // 只放慢，不短路（继续 ServeFile）
        });
        var part = PartPath("resume");
        var cp = CheckpointPath("resume");

        // 第一次：中途取消。等待「断点文件已落地」再取消：断点由监控循环（~1s tick）写入，
        // 文件存在 = 下载仍在进行（未完成），且取消后断点必然保留——消除"取消太早（无断点）/
        // 太晚（已下完取消不生效）"的竞态
        using var cts = new CancellationTokenSource();
        var dl1 = CreateDownloader("/resume.bin", readTimeout: TimeSpan.FromSeconds(2));
        var task1 = dl1.DownloadAsync(part, cp, ct: cts.Token);
        await WaitUntilAsync(() => _server.RequestCount("/resume.bin") >= 5 && File.Exists(cp), TimeSpan.FromSeconds(20),
            () => $"requests={_server.RequestCount("/resume.bin")}, cp={File.Exists(cp)}");
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task1);

        Assert.True(File.Exists(cp), "取消后应保留断点");
        var checkpoint = DownloadCheckpoint.TryLoad(cp);
        Assert.NotNull(checkpoint);
        Assert.Equal(content.Length, checkpoint!.TotalSize);

        // 第二次：断点恢复下载
        var dl2 = CreateDownloader("/resume.bin", readTimeout: TimeSpan.FromSeconds(2));
        await dl2.DownloadAsync(part, cp);

        Assert.Equal(content, File.ReadAllBytes(part));
        Assert.False(File.Exists(cp), "完成后应删除断点文件");
    }

    [Fact]
    public async Task Download_RateLimited_ThenSucceeds()
    {
        var content = MakeContent(500_000);
        _server.AddFile("/limit.bin", content);
        _server.AddBehavior(async (ctx, srv) =>
        {
            if (ctx.Request.Url!.AbsolutePath != "/limit.bin")
            {
                return false;
            }

            // 前两次请求返回 429（限流），之后放行
            if (srv.RequestCount("/limit.bin") < 2)
            {
                ctx.Response.StatusCode = 429;
                ctx.Response.Close();
                return true;
            }

            return false;
        });

        var dl = CreateDownloader("/limit.bin", readTimeout: TimeSpan.FromSeconds(2));
        var part = PartPath("limit");
        await dl.DownloadAsync(part, CheckpointPath("limit"));

        Assert.Equal(content, File.ReadAllBytes(part));
        Assert.True(_server.RequestCount("/limit.bin") >= 3, "限流后应重试成功");
    }

    [Fact]
    public async Task Download_Fatal403_FallsBackToNextUrl()
    {
        var content = MakeContent(200_000);
        _server.AddFile("/good.bin", content);
        _server.AddBehavior(async (ctx, srv) =>
        {
            if (ctx.Request.Url!.AbsolutePath != "/bad.bin")
            {
                return false;
            }

            ctx.Response.StatusCode = 403;
            ctx.Response.Close();
            return true;
        });

        var dl = new Downloader(new DownloaderOptions
        {
            Urls = new[] { _server.BaseUrl + "bad.bin", _server.BaseUrl + "good.bin" },
            ReadTimeout = TimeSpan.FromSeconds(2),
        });
        var part = PartPath("fb");
        await dl.DownloadAsync(part, CheckpointPath("fb"));

        Assert.Equal(content, File.ReadAllBytes(part));
        Assert.True(_server.RequestCount("/bad.bin") >= 1);
        Assert.True(_server.RequestCount("/good.bin") >= 1);
    }

    [Fact]
    public async Task Download_AllUrlsFail_ThrowsFatal()
    {
        _server.AddBehavior(async (ctx, srv) =>
        {
            ctx.Response.StatusCode = 403;
            ctx.Response.Close();
            return true;
        });

        var dl = new Downloader(new DownloaderOptions
        {
            Urls = new[] { _server.BaseUrl + "a.bin", _server.BaseUrl + "b.bin" },
        });
        var ex = await Assert.ThrowsAsync<DownloadException>(() => dl.DownloadAsync(PartPath("x"), CheckpointPath("x")));
        Assert.Equal(DownloadErrorKind.Fatal, ex.Kind);
    }

    [Fact]
    public async Task Download_PauseResume_Completes()
    {
        var content = MakeContent(4 * 1024 * 1024);
        _server.AddFile("/pause.bin", content);

        // 每个响应延迟 300ms：本地服务器在快机器上几十毫秒就能传完 4MB，
        // 暂停很可能落在下载完成之后（断点已被删除）→ 断言偶发失败。
        // 这里把下载拉长到多个波次，保证暂停一定发生在下载进行中。
        _server.AddBehavior(async (ctx, srv) =>
        {
            if (ctx.Request.Url!.AbsolutePath != "/pause.bin")
            {
                return false;
            }

            await Task.Delay(300);
            return false; // 延迟后交给默认文件响应
        });

        var dl = CreateDownloader("/pause.bin", readTimeout: TimeSpan.FromSeconds(2));
        var part = PartPath("pause");
        var cp = CheckpointPath("pause");
        var task = dl.DownloadAsync(part, cp);
        await WaitUntilAsync(
            () => _server.RequestCount("/pause.bin") >= 2,
            TimeSpan.FromSeconds(15),
            () => string.Join(" | ", _server.Requests.Select(r => $"{r.Path} [{r.Range}] {r.StatusCode}")));

        await dl.PauseAsync();
        await WaitUntilAsync(
            () => File.Exists(cp) || task.IsCompleted,
            TimeSpan.FromSeconds(5),
            () => "暂停后断点未落地");
        Assert.False(task.IsCompleted, "下载在暂停生效前已完成，测试时序失效");
        Assert.True(File.Exists(cp), "暂停时应持久化断点");

        // 等暂停瞬间的在途分片落地后再采样，避免把「在途写入」误判成「暂停期间继续下载」
        await Task.Delay(1000);
        var bytesAtPause = new FileInfo(part).Length;
        await Task.Delay(1500);
        Assert.Equal(bytesAtPause, new FileInfo(part).Length);

        await dl.ResumeAsync();
        await task;
        Assert.Equal(content, File.ReadAllBytes(part));
        Assert.False(File.Exists(cp));
    }

    [Fact]
    public async Task Download_CutConnection_ReconnectsAndCompletes()
    {
        var content = MakeContent(1024 * 1024);
        _server.AddFile("/cut.bin", content);
        _server.AddBehavior(async (ctx, srv) =>
        {
            // 只对探测请求（Range=bytes=0-32767，仅发出一次）触发断流，
            // 不能按"第一个请求"匹配：并发下分片请求可能先被服务器处理
            if (ctx.Request.Url!.AbsolutePath != "/cut.bin"
                || ctx.Request.Headers["Range"] != "bytes=0-32767"
                || srv.RequestCount("/cut.bin") != 0)
            {
                return false;
            }

            // 探测响应声明 32KB 但只写 16KB 后关闭连接 → client 读到 EOF 提前 → 断流。
            // 不用 Response.Abort()：HttpListener 的 Abort 在 Windows 上有偶发进程崩溃问题。
            var real = srv.GetFile("/cut.bin")!;
            ctx.Response.StatusCode = 206;
            ctx.Response.AddHeader("x-bs-file-size", real.Length.ToString());
            ctx.Response.AddHeader("Content-Range", $"bytes 0-32767/{real.Length}");
            ctx.Response.ContentLength64 = 32 * 1024;
            await ctx.Response.OutputStream.WriteAsync(real.AsMemory(0, 16 * 1024));
            ctx.Response.Close();
            return true;
        });

        var dl = CreateDownloader("/cut.bin", readTimeout: TimeSpan.FromSeconds(1));
        var part = PartPath("cut");
        await dl.DownloadAsync(part, CheckpointPath("cut"));

        var actual = File.ReadAllBytes(part);
        if (!actual.SequenceEqual(content))
        {
            var dump = string.Join("\n", _server.Requests.Select(r => $"{r.Path} Range={r.Range} -> {r.StatusCode} {r.ContentRange}"));
            Assert.Fail($"内容不一致\n请求日志:\n{dump}");
        }

        Assert.True(_server.RequestCount("/cut.bin") >= 2, "断流后应重连");
    }

    [Fact]
    public async Task Download_Progress_ReportedAndFinalComplete()
    {
        var content = MakeContent(1024 * 1024);
        _server.AddFile("/prog.bin", content);

        var dl = CreateDownloader("/prog.bin");
        var part = PartPath("prog");
        var progress = new List<DownloaderProgress>();
        await dl.DownloadAsync(part, CheckpointPath("prog"), new Progress<DownloaderProgress>(p =>
        {
            lock (progress)
            {
                progress.Add(p);
            }
        }));

        lock (progress)
        {
            Assert.NotEmpty(progress);
            Assert.All(progress, p => Assert.InRange(p.Percent, 0, 100));
            var last = progress[^1];
            Assert.Equal(content.Length, last.DownloadedBytes);
            Assert.Equal(content.Length, last.TotalBytes);
            Assert.Equal(100, last.Percent);
        }
    }

    // ======================== 断点自愈（v0.6.2）========================
    // 损坏的断点会让恢复后的文件出现空洞，而长度校验查不出来（偏移写补零），
    // 因此引擎必须能识别「不可用断点」并丢弃它从零重下。

    [Fact]
    public async Task Download_CheckpointRangesOutOfRange_RestartsFromScratch()
    {
        var content = MakeContent(600 * 1024); // 256KB 分片 → 3 块
        _server.AddFile("/badcp.bin", content);
        var part = PartPath("badcp");
        var cp = CheckpointPath("badcp");

        // 造一个区间越界的断点 + 一个长度对不上的 .part
        new DownloadCheckpoint
        {
            TotalSize = content.Length,
            BlockSize = 256 * 1024,
            GenBegin = content.Length,
            Ranges = new List<DownloadRange> { new(10, content.Length + 4096) },
        }.Save(cp);
        File.WriteAllBytes(part, new byte[1024]);

        using var dl = CreateDownloader("/badcp.bin");
        await dl.DownloadAsync(part, cp);

        Assert.Equal(content, File.ReadAllBytes(part));
        Assert.False(File.Exists(cp), "完成后应删除断点");
    }

    [Fact]
    public async Task Download_CheckpointWithoutPartFile_RestartsFromScratch()
    {
        var content = MakeContent(600 * 1024);
        _server.AddFile("/nopart.bin", content);
        var part = PartPath("nopart");
        var cp = CheckpointPath("nopart");

        // 断点在、数据文件被删：无法续传
        new DownloadCheckpoint
        {
            TotalSize = content.Length,
            BlockSize = 256 * 1024,
            GenBegin = content.Length,
            Ranges = new List<DownloadRange> { new(0, content.Length) },
        }.Save(cp);

        using var dl = CreateDownloader("/nopart.bin");
        await dl.DownloadAsync(part, cp);

        Assert.Equal(content, File.ReadAllBytes(part));
    }

    [Fact]
    public async Task Download_CheckpointClaimsDoneButFileShort_RestartsFromScratch()
    {
        var content = MakeContent(600 * 1024);
        _server.AddFile("/short.bin", content);
        var part = PartPath("short");
        var cp = CheckpointPath("short");

        // 断点声称全部下完（无剩余分片），但 .part 只有 100 字节 —— 上次「写完但长度不对」
        new DownloadCheckpoint
        {
            TotalSize = content.Length,
            BlockSize = 256 * 1024,
            GenBegin = content.Length,
            Ranges = new List<DownloadRange>(),
        }.Save(cp);
        File.WriteAllBytes(part, new byte[100]);

        using var dl = CreateDownloader("/short.bin");
        await dl.DownloadAsync(part, cp);

        Assert.Equal(content, File.ReadAllBytes(part));
    }

    [Fact]
    public async Task Download_ValidCheckpoint_StillResumes()
    {
        // 对照实验：合法断点必须继续走续传，而不是被自愈逻辑误判成「损坏」而重下
        var content = MakeContent(1024 * 1024); // 4 块
        _server.AddFile("/valid.bin", content);
        var part = PartPath("valid");
        var cp = CheckpointPath("valid");

        // 前 2 块已落盘，剩余 [512KB, 1MB)
        File.WriteAllBytes(part, content[..(512 * 1024)]);
        new DownloadCheckpoint
        {
            TotalSize = content.Length,
            BlockSize = 256 * 1024,
            GenBegin = 512 * 1024,
            Ranges = new List<DownloadRange> { new(512 * 1024, content.Length) },
        }.Save(cp);

        using var dl = CreateDownloader("/valid.bin");
        await dl.DownloadAsync(part, cp);

        Assert.Equal(content, File.ReadAllBytes(part));
        // 续传只需取后半段：请求的 Range 不应从 0 开始
        Assert.DoesNotContain(_server.Requests, r => r.Range is not null && r.Range.StartsWith("bytes=0-"));
    }
}
