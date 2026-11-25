using System.Net;
using System.Net.Http.Headers;
using BaiduPcs.Core.Api;
using BaiduPcs.Core.Transfer;

namespace BaiduPcs.Core.Download;

/// <summary>下载引擎配置。</summary>
public sealed class DownloaderOptions
{
    /// <summary>备选直链（按序尝试：失败换下一个，断点保护已下进度）。</summary>
    public IReadOnlyList<string> Urls { get; init; } = Array.Empty<string>();

    /// <summary>最大并发分片数（统一默认 8：会员 8 并发 ≈ 20MB/s，非会员服务端总量限速无副作用）。</summary>
    public int MaxParallel { get; init; } = 8;

    /// <summary>下载请求 UA（统一 netdisk 安卓 UA，与官方客户端身份一致）。</summary>
    public string UserAgent { get; init; } = ApiConstants.NetdiskUa;

    /// <summary>Cookie 头（可为空）。</summary>
    public string? CookieHeader { get; init; }

    /// <summary>Referer 头。</summary>
    public string? Referer { get; init; } = "https://pan.baidu.com/";

    /// <summary>分片读超时：无数据超过该时长判定断流并重连。</summary>
    public TimeSpan ReadTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>单分片连续失败重试上限（超过则升级为致命错误换直链）。</summary>
    public int MaxChunkRetries { get; init; } = 8;

    /// <summary>服务端不支持 Range（HTTP 200）时是否允许单线程降级。</summary>
    public bool AllowSingleStreamFallback { get; init; } = true;
}

/// <summary>下载进度信息。</summary>
public sealed class DownloaderProgress
{
    public long DownloadedBytes { get; init; }
    public long TotalBytes { get; init; }
    public double Percent => TotalBytes > 0 ? DownloadedBytes * 100.0 / TotalBytes : 0;
    public double SpeedBytesPerSecond { get; init; }
}

/// <summary>
/// 多线程分片下载引擎（对齐 Go requester/downloader）。
/// 与百度协议解耦：只负责把直链内容下载到 .part，支持断点续传（.part.json）、
/// 并发分片、断流重连、限流退避、备选直链与暂停/恢复/取消。
/// </summary>
public sealed class Downloader : IDisposable, ITransferEngine
{
    private const long ProbeSize = 32 * 1024;   // 大小探测请求范围（对齐 Go pcsdownload.go:24）
    private const int BufferSize = 128 * 1024;
    private const int MonitorIntervalMs = 500;
    private const int CheckpointIntervalMs = 1000;

    private readonly DownloaderOptions _options;
    private readonly DownloaderContext _ctx;
    private readonly HttpClient _client;

    public Downloader(DownloaderOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (options.Urls.Count == 0)
        {
            throw new ArgumentException("至少需要一个直链", nameof(options));
        }

        _ctx = new DownloaderContext
        {
            Client = CreateClient(options.MaxParallel),
            UserAgent = options.UserAgent,
            CookieHeader = options.CookieHeader,
            Referer = options.Referer,
            ReadTimeout = options.ReadTimeout,
            MaxChunkRetries = options.MaxChunkRetries,
        };
        _client = _ctx.Client;
    }

    /// <summary>
    /// 下载直链内容到 partPath（偏移写，可断点续传）。
    /// checkpointPath 为断点元数据文件（name.part.json），完成时删除，失败/取消时保留。
    /// 完成 .part 后的重命名由调用方负责。
    /// </summary>
    public async Task DownloadAsync(
        string partPath,
        string checkpointPath,
        IProgress<DownloaderProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(checkpointPath);

        // 备选直链按序尝试：失败换下一个 URL 重跑（断点恢复保护已下进度）
        DownloadException? lastError = null;
        foreach (var url in _options.Urls)
        {
            try
            {
                _ctx.CurrentUrl = url;
                await DownloadWithUrlAsync(partPath, checkpointPath, progress, ct).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (DownloadException ex)
            {
                lastError = ex;
            }
        }

        throw lastError ?? new DownloadException("下载失败");
    }

    /// <summary>暂停：worker 在当前分片边界挂起，立即持久化断点。下载未运行时无效果。</summary>
    public Task PauseAsync()
    {
        _ctx.PauseGate.Reset();
        _ctx.SaveCheckpoint();
        return Task.CompletedTask;
    }

    /// <summary>恢复下载。</summary>
    public Task ResumeAsync()
    {
        _ctx.PauseGate.Set();
        return Task.CompletedTask;
    }

    public void Dispose() => _client.Dispose();

    // ======================== 单 URL 尝试 ========================

    private async Task DownloadWithUrlAsync(
        string partPath,
        string checkpointPath,
        IProgress<DownloaderProgress>? progress,
        CancellationToken ct)
    {
        var checkpoint = DownloadCheckpoint.TryLoad(checkpointPath);
        if (checkpoint is not null && !IsCheckpointUsable(checkpoint, partPath))
        {
            // 断点与磁盘数据不一致（区间越界 / 数据文件缺失 / 声称已下完但长度不符）：
            // 丢弃断点、从零重下。保留这种断点会让 .part 出现空洞，而长度校验查不出来
            // （偏移写会补零），最终表现为「下载成功但文件损坏」。
            checkpoint = null;
            TryDeleteFile(checkpointPath);
        }

        long totalSize;
        HttpResponseMessage? probe = null;
        var singleStream = false;

        if (checkpoint is not null)
        {
            // 恢复模式：直链用新传入的，断点提供大小/分片/进度
            totalSize = checkpoint.TotalSize;
        }
        else
        {
            var probeResult = await ProbeAsync(_ctx.CurrentUrl!, ct).ConfigureAwait(false);
            totalSize = probeResult.TotalSize;
            singleStream = probeResult.SingleStream;
            probe = probeResult.Response;
        }

        if (singleStream)
        {
            // 服务端不支持 Range：单线程流式从头下载，不记断点（对齐 Go 单线程分支）
            try
            {
                await DownloadSingleStreamAsync(partPath, progress, ct).ConfigureAwait(false);
                File.Delete(checkpointPath);
            }
            catch (DownloadException ex) when (ex.Kind == DownloadErrorKind.SingleStreamFallback)
            {
                // 单线程内不再降级
                throw;
            }

            return;
        }

        if (totalSize <= 0)
        {
            // 空文件：直接落盘完成
            await File.WriteAllBytesAsync(partPath, Array.Empty<byte>(), ct).ConfigureAwait(false);
            File.Delete(checkpointPath);
            progress?.Report(new DownloaderProgress { DownloadedBytes = 0, TotalBytes = 0 });
            return;
        }

        var blockSize = checkpoint?.BlockSize > 0 ? checkpoint.BlockSize : BlockSizes.Select(totalSize);
        var gen = new RangeGenerator(totalSize, blockSize, checkpoint?.GenBegin ?? 0);
        var workers = new List<ChunkWorker>();
        var completedOk = false;

        // .part 以偏移写打开（FileShare.Read 允许外部读取进度）。
        // 没有可用断点 = 全新下载：用 Create 截断旧 .part，避免残留尾部让长度校验变成假象。
        var openMode = checkpoint is null ? FileMode.Create : FileMode.OpenOrCreate;
        using (var file = File.OpenHandle(partPath, openMode, FileAccess.Write, FileShare.Read))
        {
            _ctx.File = file;
            _ctx.TotalSize = totalSize;
            _ctx.BlockSize = blockSize;
            _ctx.CheckpointPath = checkpointPath;
            _ctx.Gen = gen;
            _ctx.Workers = workers;
            _ctx.Stopping = false;
            // 恢复时已下载量 = 已分配 - 剩余（对齐 Go download_instanceinfo.go）
            _ctx.DownloadedBytes = checkpoint is null
                ? 0
                : Math.Max(0, checkpoint.GenBegin - checkpoint.Ranges.Sum(r => r.Length));

            var parallel = Math.Max(1, Math.Min(_options.MaxParallel, (int)(totalSize / BlockSizes.MinParallelSize) + 1));

            if (checkpoint is { Ranges.Count: > 0 })
            {
                // 恢复：并发数 = 剩余分片数（对齐 Go SelectParallel），进度不丢
                for (var i = 0; i < checkpoint.Ranges.Count; i++)
                {
                    var w = new ChunkWorker(_ctx, ct);
                    workers.Add(w);
                    w.Assign(checkpoint.Ranges[i]);
                }
            }
            else
            {
                // 全新：初始分配 parallel 个分片，其余由监控循环补片
                // 取片 + Assign 整体持 CheckpointSync 锁，避免快照读到"已分配未落 worker"的空隙
                lock (_ctx.CheckpointSync)
                {
                    for (var i = 0; i < parallel; i++)
                    {
                        if (!gen.TryGenerate(out var r))
                        {
                            break;
                        }

                        var w = new ChunkWorker(_ctx, ct);
                        workers.Add(w);
                        w.Assign(r);
                    }
                }
            }

            if (probe is not null && workers.Count > 0)
            {
                workers[0].AttachInitialResponse(probe);
            }

            try
            {
                await RunMonitorAsync(workers, gen, totalSize, progress, ct).ConfigureAwait(false);

                // 监控正常退出 = 全部完成：校验文件长度
                var actual = RandomAccess.GetLength(file);
                if (actual != totalSize)
                {
                    throw new DownloadException($"下载不完整: 期望 {totalSize} 字节, 实际 {actual}");
                }

                completedOk = true;
                File.Delete(checkpointPath); // 成功：清理断点
                progress?.Report(new DownloaderProgress
                {
                    DownloadedBytes = totalSize,
                    TotalBytes = totalSize,
                    SpeedBytesPerSecond = _ctx.Speed.GetBytesPerSecond(),
                });
            }
            finally
            {
                if (!completedOk)
                {
                    _ctx.SaveCheckpoint(); // 失败/取消：保留最新断点
                }

                await StopWorkersAsync(workers).ConfigureAwait(false);
            }
        }
    }

    // ======================== 监控循环 ========================

    /// <summary>
    /// 监控循环（对齐 Go monitor.go）：补片、worker 失败传播、断点持久化、进度上报、完成判定。
    /// 正常退出 = 全部完成；worker 失败/取消时抛对应异常。
    /// </summary>
    private async Task RunMonitorAsync(
        List<ChunkWorker> workers,
        RangeGenerator gen,
        long totalSize,
        IProgress<DownloaderProgress>? progress,
        CancellationToken ct)
    {
        var lastCheckpoint = Environment.TickCount64;

        while (true)
        {
            // 取消必须即时生效：Task.WhenAny 自身总是成功完成（返回先完成的任务），
            // 取消后 Delay/needWork 立即以 canceled 完成 → WhenAny 立即返回 → 空转。
            ct.ThrowIfCancellationRequested();

            var needWork = _ctx.NeedWork.WaitAsync(ct);
            var winner = await Task.WhenAny(Task.Delay(MonitorIntervalMs, ct), needWork).ConfigureAwait(false);
            if (winner.IsCanceled)
            {
                // 任一等待因取消完成（Task.Delay / WaitAsync 在 ct 取消时返回 canceled）
                ct.ThrowIfCancellationRequested();
            }

            _ctx.NeedWork.Reset();

            // worker 失败 → 传播原始异常（主控换直链或失败）
            foreach (var w in workers)
            {
                if (w.Completion.IsFaulted)
                {
                    await w.Completion.ConfigureAwait(false);
                }
            }

            // 补片：给空闲 worker 分配新分片（与断点快照互斥，见 CheckpointSync 注释）
            lock (_ctx.CheckpointSync)
            {
                foreach (var w in workers)
                {
                    if (w.HasRange)
                    {
                        continue;
                    }

                    if (!gen.TryGenerate(out var r))
                    {
                        break;
                    }

                    w.Assign(r);
                }
            }

            // 完成判定：无未分配分片且所有 worker 空闲
            if (gen.IsDone && workers.All(w => !w.HasRange))
            {
                return;
            }

            // 断点持久化（~1s 间隔，对齐 Go 990ms tick）
            var now = Environment.TickCount64;
            if (now - lastCheckpoint >= CheckpointIntervalMs)
            {
                _ctx.SaveCheckpoint();
                lastCheckpoint = now;
            }

            progress?.Report(new DownloaderProgress
            {
                DownloadedBytes = Volatile.Read(ref _ctx.DownloadedBytes),
                TotalBytes = totalSize,
                SpeedBytesPerSecond = _ctx.Speed.GetBytesPerSecond(),
            });
        }
    }

    private async Task StopWorkersAsync(List<ChunkWorker> workers)
    {
        _ctx.Stopping = true;
        _ctx.PauseGate.Set();
        _ctx.NeedWork.Set();
        foreach (var w in workers)
        {
            w.Stop();
        }

        try
        {
            await Task.WhenAll(workers.Select(w => w.Completion)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 停止阶段忽略 worker 退出异常
        }
        catch (DownloadException)
        {
        }
    }

    // ======================== 大小探测 ========================

    private sealed record ProbeResult(long TotalSize, HttpResponseMessage? Response, bool SingleStream);

    /// <summary>
    /// 大小探测（对齐 Go BaiduPCSURLCheckFunc）：GET Range: bytes=0-32767，
    /// 优先读 x-bs-file-size 头取总大小；206 响应体复用为 0 号分片；200 表示不支持 Range → 单线程。
    /// </summary>
    private async Task<ProbeResult> ProbeAsync(string url, CancellationToken ct)
    {
        var retries = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            while (!_ctx.ConnectionLimiter.TryAcquire())
            {
                await Task.Delay(500, ct).ConfigureAwait(false);
            }

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url)
                {
                    Headers = { Range = new RangeHeaderValue(0, ProbeSize - 1) },
                };
                AddCommonHeaders(request);
                var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

                var kind = DownloadErrorClassifier.Classify(response.StatusCode);
                if (kind == DownloadErrorKind.None)
                {
                    if (response.StatusCode == HttpStatusCode.OK)
                    {
                        response.Dispose();
                        return new ProbeResult(0, null, SingleStream: true);
                    }

                    var total = GetTotalSize(response);
                    if (total <= 0)
                    {
                        response.Dispose();
                        throw new DownloadException("无法确定文件大小（缺少 x-bs-file-size / Content-Range）");
                    }

                    return new ProbeResult(total, response, SingleStream: false);
                }

                response.Dispose();
                if (kind == DownloadErrorKind.Fatal)
                {
                    throw new DownloadException($"探测失败: HTTP {(int)response.StatusCode}", DownloadErrorKind.Fatal);
                }

                if (++retries > _ctx.MaxChunkRetries)
                {
                    throw new DownloadException("探测重试次数过多", kind);
                }

                var wait = kind == DownloadErrorKind.RateLimited
                    ? DownloadRetryPolicy.RateLimitWait(retries)
                    : DownloadRetryPolicy.NetworkWait(retries);
                await Task.Delay(wait, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException) when (++retries <= _ctx.MaxChunkRetries)
            {
                await Task.Delay(DownloadRetryPolicy.NetworkWait(retries), ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new DownloadException("探测网络错误重试次数过多", DownloadErrorKind.Network, ex);
            }
        }
    }

    /// <summary>从响应头取文件总大小：x-bs-file-size 优先，其次 Content-Range。</summary>
    private static long GetTotalSize(HttpResponseMessage response)    {
        if (response.Headers.TryGetValues("x-bs-file-size", out var values)
            && long.TryParse(values.FirstOrDefault(), out var size)
            && size > 0)
        {
            return size;
        }

        if (response.Content.Headers.ContentRange is { HasLength: true } range && range.Length is > 0)
        {
            return range.Length.Value;
        }

        return 0;
    }

    /// <summary>
    /// 断点是否可用于续传。不可用意味着「按它续传会写出带空洞的文件」，
    /// 只能丢弃断点从零重下（长度校验对此无效，因为偏移写会把空洞补成零）。
    /// </summary>
    private static bool IsCheckpointUsable(DownloadCheckpoint checkpoint, string partPath)
    {
        if (checkpoint.TotalSize <= 0 || checkpoint.BlockSize <= 0)
        {
            return false;
        }

        if (checkpoint.GenBegin < 0 || checkpoint.GenBegin > checkpoint.TotalSize)
        {
            return false;
        }

        foreach (var range in checkpoint.Ranges)
        {
            if (range.Begin < 0 || range.Length <= 0 || range.End > checkpoint.TotalSize)
            {
                return false;
            }
        }

        if (!File.Exists(partPath))
        {
            return false; // 断点在、数据文件不见了：无从续传
        }

        // 没有任何剩余分片 = 声称已下完，则数据文件长度必须吻合；否则说明上次写完但长度不对
        return checkpoint.Ranges.Count > 0 || new FileInfo(partPath).Length == checkpoint.TotalSize;
    }

    /// <summary>尽力删除（断点自愈用，失败不影响继续下载）。</summary>
    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    // ======================== 单线程降级 ========================

    /// <summary>服务端不支持 Range 时的单线程流式下载：不记断点、不重连（对齐 Go 单线程分支）。</summary>
    private async Task DownloadSingleStreamAsync(
        string partPath,
        IProgress<DownloaderProgress>? progress,
        CancellationToken ct)
    {
        await using var fs = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.Read, BufferSize, useAsync: true);
        using var request = new HttpRequestMessage(HttpMethod.Get, _ctx.CurrentUrl);
        AddCommonHeaders(request);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        var kind = DownloadErrorClassifier.Classify(response.StatusCode);
        if (kind != DownloadErrorKind.None)
        {
            throw new DownloadException($"下载失败: HTTP {(int)response.StatusCode}", kind);
        }

        var total = response.Content.Headers.ContentLength ?? 0;
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new byte[BufferSize];
        long downloaded = 0;

        while (true)
        {
            await _ctx.PauseGate.WaitAsync(ct).ConfigureAwait(false);

            int read;
            try
            {
                read = await ReadWithTimeoutAsync(stream, buffer, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new DownloadException("单线程下载读取超时（该模式不重连）", DownloadErrorKind.Network);
            }

            if (read <= 0)
            {
                break;
            }

            await fs.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            downloaded += read;
            Interlocked.Add(ref _ctx.DownloadedBytes, read);
            _ctx.Speed.Add(read);
            progress?.Report(new DownloaderProgress
            {
                DownloadedBytes = downloaded,
                TotalBytes = total,
                SpeedBytesPerSecond = _ctx.Speed.GetBytesPerSecond(),
            });
        }

        await fs.FlushAsync(ct).ConfigureAwait(false);
    }

    // ======================== 工具 ========================

    /// <summary>下载客户端：无限超时（流读取由 ReadTimeout 负责）、不禁用自动重定向、不解压（解压破坏 Range 偏移）。</summary>
    private static HttpClient CreateClient(int maxParallel)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxConnectionsPerServer = Math.Max(8, maxParallel),
            ConnectTimeout = TimeSpan.FromSeconds(20),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        };
        return new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    private void AddCommonHeaders(HttpRequestMessage request)
    {
        // TryAddWithoutValidation：netdisk 安卓 UA 是非标准格式，普通 API 会解析失败静默丢弃
        request.Headers.TryAddWithoutValidation("User-Agent", _ctx.UserAgent);
        if (!string.IsNullOrEmpty(_ctx.CookieHeader))
        {
            request.Headers.TryAddWithoutValidation("Cookie", _ctx.CookieHeader);
        }

        if (!string.IsNullOrEmpty(_ctx.Referer))
        {
            request.Headers.TryAddWithoutValidation("Referer", _ctx.Referer);
        }
    }

    /// <summary>带读超时的流读取：超时抛 TimeoutException（断流检测）。</summary>
    private async Task<int> ReadWithTimeoutAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_ctx.ReadTimeout);
        try
        {
            return await stream.ReadAsync(buffer, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("读取超时");
        }
    }
}
