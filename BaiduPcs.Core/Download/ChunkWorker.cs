using System.Net;
using System.Net.Http.Headers;

namespace BaiduPcs.Core.Download;

/// <summary>
/// 单分片下载 worker（对齐 Go requester/downloader/worker.go）。
/// 常驻循环：等待分配分片 → 下载（206 校验 / 读超时断流重连 / 暂停门控 / 偏移写盘）→ 等待下一片。
/// 分片内网络错误自动重连（从当前 Begin 续传）；致命错误（403/404/416）抛 DownloadException 交给主控换直链。
/// </summary>
internal sealed class ChunkWorker
{
    private const int BufferSize = 128 * 1024;

    private readonly DownloaderContext _ctx;
    private readonly AsyncManualResetEvent _assigned = new();
    private readonly Task _task;

    private long _rangeBegin;
    private long _rangeEnd;
    private bool _hasRange; // 一律经 Volatile.Read/Write 访问
    private HttpResponseMessage? _initialResponse;

    public ChunkWorker(DownloaderContext ctx, CancellationToken ct)
    {
        _ctx = ctx;
        _task = Task.Run(() => RunLoopAsync(ct));
    }

    /// <summary>worker 任务（异常时携带 DownloadException 等原始异常）。</summary>
    public Task Completion => _task;

    /// <summary>是否持有未完成分片（下载中 / 等待重连）。</summary>
    public bool HasRange => Volatile.Read(ref _hasRange);

    /// <summary>当前剩余分片 [Begin, End)（监控循环与断点保存线程安全读取）。</summary>
    public DownloadRange RemainingRange => _hasRange
        ? new DownloadRange(Volatile.Read(ref _rangeBegin), Volatile.Read(ref _rangeEnd))
        : default;

    /// <summary>分配一个分片（可从监控循环调用）。</summary>
    public void Assign(DownloadRange range)
    {
        Volatile.Write(ref _rangeBegin, range.Begin);
        Volatile.Write(ref _rangeEnd, range.End);
        Volatile.Write(ref _hasRange, true);
        _assigned.Set();
    }

    /// <summary>附加探测响应流（仅 0 号 worker 复用探测连接，省一次请求）。</summary>
    public void AttachInitialResponse(HttpResponseMessage response) => _initialResponse = response;

    /// <summary>唤醒 worker 检查退出标志（主控停止时调用）。</summary>
    public void Stop() => _assigned.Set();

    private async Task RunLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!_ctx.Stopping)
            {
                await _ctx.PauseGate.WaitAsync(ct).ConfigureAwait(false);

                // 等待分配。内层循环消费残留信号，避免忙等。
                while (!_ctx.Stopping)
                {
                    if (TryTakeRange(out var range))
                    {
                        try
                        {
                            await DownloadRangeAsync(range, ct).ConfigureAwait(false);
                        }
                        finally
                        {
                            _ctx.NeedWork.Set(); // 通知监控补片
                        }

                        break;
                    }

                    await _assigned.WaitAsync(ct).ConfigureAwait(false);
                    _assigned.Reset();
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 取消：正常退出
        }
    }

    private bool TryTakeRange(out DownloadRange range)
    {
        if (!Volatile.Read(ref _hasRange))
        {
            range = default;
            return false;
        }

        range = new DownloadRange(Volatile.Read(ref _rangeBegin), Volatile.Read(ref _rangeEnd));
        return true; // _hasRange 保持 true：该分片处于下载中，断点保存需要它
    }

    private async Task DownloadRangeAsync(DownloadRange range, CancellationToken ct)
    {
        var begin = range.Begin;
        var end = range.End;
        var retries = 0;

        // 探测响应复用（仅 0 号 worker 首次调用）
        var response = _initialResponse;
        _initialResponse = null;

        try
        {
            while (begin < end)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    if (response is null)
                    {
                        // 新连接受全局限流约束（9s 窗口 80 次）
                        while (!_ctx.ConnectionLimiter.TryAcquire())
                        {
                            await Task.Delay(500, ct).ConfigureAwait(false);
                        }

                        response = await SendRangeRequestAsync(begin, end, ct).ConfigureAwait(false);
                    }

                    var kind = DownloadErrorClassifier.Classify(response.StatusCode);
                    if (kind == DownloadErrorKind.Fatal)
                    {
                        throw new DownloadException($"下载失败: HTTP {(int)response.StatusCode}", DownloadErrorKind.Fatal);
                    }

                    if (kind == DownloadErrorKind.RateLimited)
                    {
                        response.Dispose();
                        response = null;
                        await Task.Delay(DownloadRetryPolicy.RateLimitWait(++retries), ct).ConfigureAwait(false);
                        continue;
                    }

                    if (kind == DownloadErrorKind.Network)
                    {
                        response.Dispose();
                        response = null;
                        if (++retries > _ctx.MaxChunkRetries)
                        {
                            throw new DownloadException("HTTP 错误重试次数过多", DownloadErrorKind.Network);
                        }

                        await Task.Delay(DownloadRetryPolicy.NetworkWait(retries), ct).ConfigureAwait(false);
                        continue;
                    }

                    // 2xx 且非 206：服务端忽略 Range 返回 200 全量 → 主控单线程降级
                    if (response.StatusCode == HttpStatusCode.OK)
                    {
                        throw new DownloadException("服务端不支持 Range（HTTP 200），需要单线程降级", DownloadErrorKind.SingleStreamFallback);
                    }

                    // 206：流式读取并偏移写盘
                    await using (var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
                    {
                        var buffer = new byte[BufferSize];
                        while (begin < end)
                        {
                            await _ctx.PauseGate.WaitAsync(ct).ConfigureAwait(false);

                            var read = await ReadWithTimeoutAsync(stream, buffer, ct).ConfigureAwait(false);
                            if (read <= 0)
                            {
                                break; // EOF 早于范围结束：断流 → 重连
                            }

                            await _ctx.WriteAtAsync(buffer.AsMemory(0, read), begin, ct).ConfigureAwait(false);
                            begin += read;
                            Volatile.Write(ref _rangeBegin, begin);
                            Interlocked.Add(ref _ctx.DownloadedBytes, read);
                            _ctx.Speed.Add(read);
                        }
                    }

                    response.Dispose();
                    response = null;

                    if (begin < end)
                    {
                        if (++retries > _ctx.MaxChunkRetries)
                        {
                            throw new DownloadException("连接提前结束重试次数过多", DownloadErrorKind.Network);
                        }

                        await Task.Delay(DownloadRetryPolicy.NetworkWait(retries), ct).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (TimeoutException)
                {
                    // 读超时（CDN 断流）：重连
                    response?.Dispose();
                    response = null;
                    if (++retries > _ctx.MaxChunkRetries)
                    {
                        throw new DownloadException("读取超时重试次数过多", DownloadErrorKind.Network);
                    }

                    await Task.Delay(DownloadRetryPolicy.NetworkWait(retries), ct).ConfigureAwait(false);
                }
                catch (HttpRequestException ex)
                {
                    response?.Dispose();
                    response = null;
                    if (++retries > _ctx.MaxChunkRetries)
                    {
                        throw new DownloadException("网络错误重试次数过多", DownloadErrorKind.Network, ex);
                    }

                    await Task.Delay(DownloadRetryPolicy.NetworkWait(retries), ct).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            response?.Dispose();
            if (begin >= end)
            {
                Volatile.Write(ref _rangeBegin, end);
                Volatile.Write(ref _hasRange, false); // 该分片完成
            }
        }
    }

    private async Task<HttpResponseMessage> SendRangeRequestAsync(long begin, long end, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, _ctx.CurrentUrl)
        {
            Headers = { Range = new RangeHeaderValue(begin, end - 1) },
        };
        AddCommonHeaders(request);
        return await _ctx.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
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
