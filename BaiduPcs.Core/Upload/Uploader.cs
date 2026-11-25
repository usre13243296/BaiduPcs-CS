using BaiduPcs.Core.Api;
using BaiduPcs.Core.Download;
using BaiduPcs.Core.Transfer;
using BaiduPcs.Core.Util;

namespace BaiduPcs.Core.Upload;

/// <summary>上传引擎配置。</summary>
public sealed class UploaderOptions
{
    /// <summary>协议层客户端（precreate / superfile2 / create / meta 探测）。</summary>
    public required BaiduPcsApiClient Api { get; init; }

    /// <summary>本地文件路径。</summary>
    public required string LocalFilePath { get; init; }

    /// <summary>远端目标目录（如 /我的文件；根目录传 /）。</summary>
    public required string RemoteDir { get; init; }

    /// <summary>目标文件名（默认取本地文件名；重名时自动递增 2/3/4…n）。</summary>
    public string? FileName { get; init; }

    /// <summary>最大并发分片数（对齐 Go MaxUploadParallel=4）。</summary>
    public int MaxParallel { get; init; } = 4;

    /// <summary>单分片连续失败重试上限（超过则整个任务失败，可由任务层重试）。</summary>
    public int MaxChunkRetries { get; init; } = 8;
}

/// <summary>上传进度信息。</summary>
public sealed class UploadProgress
{
    public long UploadedBytes { get; init; }
    public long TotalBytes { get; init; }
    public double Percent => TotalBytes > 0 ? UploadedBytes * 100.0 / TotalBytes : 0;
    public double SpeedBytesPerSecond { get; init; }
}

/// <summary>
/// 上传引擎：秒传预检（precreate 二合一）→ 分片并发上传（superfile2）→ 合并（create）。
/// 同名文件自动递增重命名；进程内断点续传（复用 UploadSession：uploadid + 已传分片跳过）；
/// 服务端会话失效（31363）自动重新 precreate 整体重传一轮。
/// 对齐 Go internal/pcsfunctions/pcsupload（upload_task_unit.go + multiworker.go）。
/// </summary>
public sealed class Uploader : IDisposable, ITransferEngine
{
    private readonly UploaderOptions _options;
    private readonly UploadSession _session;
    private readonly AsyncManualResetEvent _pauseGate = new(signaled: true);
    private readonly SpeedMeter _speedMeter = new();

    private string? _pcsHost;
    private long? _uk;
    private string? _targetPath;
    private long _totalBytes;
    private long _uploadedBytes;

    public Uploader(UploaderOptions options, UploadSession session)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }

    /// <summary>按文件大小选分片档位（对齐 Go pcsupload/utils.go getBlockSize）。</summary>
    public static long GetBlockSize(long fileSize) => fileSize switch
    {
        >= BaiduPcsApiClient.MaxUploadThreshold => BaiduPcsApiClient.MaxUploadBlockSize,
        >= BaiduPcsApiClient.MiddleUploadThreshold => BaiduPcsApiClient.MiddleUploadBlockSize,
        _ => BaiduPcsApiClient.MinUploadBlockSize,
    };

    /// <summary>重名处理后的最终远端目标路径（UploadAsync 执行后可用；供 UI 展示）。</summary>
    public string? TargetPath => _targetPath;

    /// <summary>本次执行是否秒传命中（return_type=2，无分片上传）；供 UI 区分展示。</summary>
    public bool WasRapidUpload { get; private set; }

    /// <summary>
    /// 执行上传。可安全重复调用（失败重试）：复用会话跳过已传分片、重名探测重新执行。
    /// 秒传命中直接返回；否则分片上传 + 合并，完成后会话保留（幂等）。
    /// 期间由监控循环（RunMonitorAsync）时间驱动刷新进度/速度显示。
    /// </summary>
    public async Task UploadAsync(IProgress<UploadProgress>? progress = null, CancellationToken ct = default)
    {
        var fileInfo = new FileInfo(_options.LocalFilePath);
        if (!fileInfo.Exists)
        {
            throw new UploadException($"本地文件不存在：{_options.LocalFilePath}", UploadErrorKind.LocalFileError);
        }

        if (fileInfo.Length > BaiduPcsApiClient.MaxUploadSize)
        {
            throw new UploadException("文件大小超过 128GB 上限，无法上传", UploadErrorKind.TooLarge);
        }

        _totalBytes = fileInfo.Length;
        _uploadedBytes = 0;

        // 监控心跳与主流程同生命周期：主流程结束（完成/失败/取消）即停止
        using var monitorCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var monitorTask = RunMonitorAsync(progress, monitorCts.Token);
        try
        {
            _pcsHost = await PickHostAsync(ct).ConfigureAwait(false);
            _targetPath = await ResolveTargetPathAsync(ct).ConfigureAwait(false);

            // 校验和预计算（本地读取，不计入上传进度——否则进度会提前打满 100%，
            // 真实上传阶段反而冻结不动；进度从分片上传开始走）
            var checksum = await UploadChecksum.ComputeAsync(
                _options.LocalFilePath, GetBlockSize(fileInfo.Length), progress: null, ct).ConfigureAwait(false);

            // 会话失效（31363）最多自动重来一轮：清会话 → 重新 precreate → 整体重传
            for (var round = 0; round < 2; round++)
            {
                ct.ThrowIfCancellationRequested();

                var uk = await GetUkAsync(ct).ConfigureAwait(false);
                var dataTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var dataOffset = DataOffsetCalculator.CalculateDataOffset(checksum.ContentMd5, uk, dataTime, checksum.Length);
                var dataContent = DataOffsetCalculator.ToBase64WithoutPadding(
                    DataOffsetCalculator.ReadDataContent(_options.LocalFilePath, dataOffset));

                var result = await _options.Api.PrecreateAsync(
                    _targetPath,
                    checksum.Length,
                    checksum.ContentMd5,
                    checksum.SliceMd5,
                    checksum.BlockMd5s,
                    dataOffset,
                    dataContent,
                    dataTime,
                    rtype: 3,
                    _session.UploadId,
                    ct).ConfigureAwait(false);

                if (result.IsRapidUpload)
                {
                    // 秒传命中：无需上传分片，进度条瞬间读满（0 → 100，方便与真实上传区分），
                    // 不计网络速度（本地瞬间完成）
                    WasRapidUpload = true;
                    ReportProgress(progress, _totalBytes - _uploadedBytes, countSpeed: false);
                    return;
                }

                if (string.IsNullOrEmpty(result.UploadId))
                {
                    throw new UploadException("预创建未返回 uploadid", UploadErrorKind.Fatal);
                }

                var uploadId = result.UploadId;
                _session.UploadId = uploadId;

                try
                {
                    await UploadBlocksAsync(checksum, progress, ct).ConfigureAwait(false);
                    await _options.Api.CreateAsync(
                        _targetPath, checksum.Length, uploadId, checksum.BlockMd5s, rtype: 3, ct).ConfigureAwait(false);
                    return;
                }
                catch (UploadException ex) when (ex.Kind == UploadErrorKind.SessionExpired && round == 0)
                {
                    // 上传状态过期：清空会话重来（对齐 Go upload_task_unit.go:360-367 删库重传）
                    _session.UploadId = null;
                    _session.UploadedBlocks.Clear();
                }
            }

            throw new UploadException("上传失败：服务端上传会话反复失效", UploadErrorKind.Fatal);
        }
        finally
        {
            monitorCts.Cancel();
            try
            {
                await monitorTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 心跳正常退出
            }
        }
    }

    /// <summary>暂停：worker 在分片边界挂起（当前分片完成后暂停），已传分片保留。</summary>
    public Task PauseAsync()
    {
        _pauseGate.Reset();
        return Task.CompletedTask;
    }

    /// <summary>恢复：放行挂起的 worker，从各自剩余分片继续。</summary>
    public Task ResumeAsync()
    {
        _pauseGate.Set();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        // 无自有资源（api client 与文件流由外部/局部管理）
    }

    // ======================== 分片上传 ========================

    private async Task UploadBlocksAsync(UploadChecksum checksum, IProgress<UploadProgress>? progress, CancellationToken ct)
    {
        var blockCount = checksum.BlockMd5s.Count;
        var uploadId = _session.UploadId!;
        var tasks = new List<Task>(blockCount);
        using var semaphore = new SemaphoreSlim(_options.MaxParallel);

        for (var seq = 0; seq < blockCount; seq++)
        {
            ct.ThrowIfCancellationRequested();

            if (_session.UploadedBlocks.ContainsKey(seq))
            {
                // 断点续传：已传分片跳过，仅补计进度（本地瞬间完成，不计网络速度）
                ReportProgress(progress, BlockLength(checksum, seq), countSpeed: false);
                continue;
            }

            await semaphore.WaitAsync(ct).ConfigureAwait(false);
            var captured = seq;
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    await UploadBlockAsync(captured, checksum, uploadId, progress, ct).ConfigureAwait(false);
                }
                finally
                {
                    semaphore.Release();
                }
            }, ct));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task UploadBlockAsync(int seq, UploadChecksum checksum, string uploadId, IProgress<UploadProgress>? progress, CancellationToken ct)
    {
        await _pauseGate.WaitAsync(ct).ConfigureAwait(false);

        var offset = (long)seq * checksum.BlockSize;
        var length = BlockLength(checksum, seq);

        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            UploadErrorKind kind = UploadErrorKind.Network;
            Exception? error = null;

            try
            {
                using var fs = new FileStream(
                    _options.LocalFilePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                    1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan);

                // 限长子流（只暴露本分片字节，避免 multipart 误发文件剩余部分）；
                // 每次尝试都插桩读取进度（读多少 ≈ 发多少）。失败重试重复读会使进度短暂虚高，
                // 由 ReportProgress 的 clamp 兜底；但保证重试/停顿期间进度可见（不黑屏）
                using var uploadStream = new ProgressReadStream(
                    fs, offset, length, read => ReportProgress(progress, read, countSpeed: true));

                // 上传流由协议层的 UploadMultipartContent 负责释放
                var md5 = await _options.Api.UploadTmpFileAsync(
                    _pcsHost ?? string.Empty, _targetPath!, uploadId, seq, offset, uploadStream, ct).ConfigureAwait(false);

                _session.UploadedBlocks[seq] = md5;
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (BaiduApiException ex)
            {
                kind = ClassifyError(ex);
                error = ex;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                kind = UploadErrorKind.LocalFileError;
                error = ex;
            }
            catch (Exception ex)
            {
                // 兜底（含响应 md5 缺失等内部错误）：按网络错误重试
                kind = UploadErrorKind.Network;
                error = ex;
            }

            if (kind == UploadErrorKind.SessionExpired)
            {
                throw new UploadException($"分片 {seq} 上传失败：上传状态过期", UploadErrorKind.SessionExpired, error);
            }

            if (kind == UploadErrorKind.Fatal)
            {
                throw new UploadException($"分片 {seq} 上传失败：{error?.Message}", UploadErrorKind.Fatal, error);
            }

            if (kind == UploadErrorKind.LocalFileError)
            {
                throw new UploadException($"本地文件读取失败：{error?.Message}", UploadErrorKind.LocalFileError, error);
            }

            if (attempt >= _options.MaxChunkRetries)
            {
                throw new UploadException($"分片 {seq} 连续失败 {attempt + 1} 次：{error?.Message}", kind, error);
            }

            await Task.Delay(Backoff(kind, attempt), ct).ConfigureAwait(false);
        }
    }

    private long BlockLength(UploadChecksum checksum, int seq)
        => Math.Min(checksum.BlockSize, checksum.Length - (long)seq * checksum.BlockSize);

    // ======================== 错误分类与退避 ========================

    /// <summary>
    /// BaiduApiException → 上传错误分类（对齐 Go upload_task_unit.go:338-397）：
    /// 31363 会话失效；HTTP 400/401/403/413 致命；429/509 限流；其余可重试。
    /// </summary>
    private static UploadErrorKind ClassifyError(BaiduApiException ex)
    {
        if (ex.ErrorType == ApiErrorType.RemoteError)
        {
            return ex.RemoteCode switch
            {
                31363 => UploadErrorKind.SessionExpired,
                31061 or 31112 => UploadErrorKind.Fatal,
                _ => UploadErrorKind.Network,
            };
        }

        // HTTP 状态错误（协议层 TryThrowRemoteError 抛 Other，消息固定为 "http 响应错误, 状态码: N"）
        if (ex.ErrorType == ApiErrorType.Other && TryGetHttpStatus(ex.Message, out var status))
        {
            return status switch
            {
                429 or 509 => UploadErrorKind.RateLimited,
                400 or 401 or 403 or 413 => UploadErrorKind.Fatal,
                _ => UploadErrorKind.Network,
            };
        }

        return UploadErrorKind.Network;
    }

    private static bool TryGetHttpStatus(string message, out int status)
    {
        const string marker = "状态码: ";
        var idx = message.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0)
        {
            status = 0;
            return false;
        }

        var rest = message[(idx + marker.Length)..];
        var end = 0;
        while (end < rest.Length && char.IsDigit(rest[end]))
        {
            end++;
        }

        if (end > 0 && int.TryParse(rest[..end], out status))
        {
            return true;
        }

        status = 0;
        return false;
    }

    private static TimeSpan Backoff(UploadErrorKind kind, int attempt)
        => kind == UploadErrorKind.RateLimited
            ? DownloadRetryPolicy.RateLimitWait(attempt + 1)
            : DownloadRetryPolicy.NetworkWait(attempt + 1);

    // ======================== 辅助 ========================

    /// <summary>重名探测：目标存在则自动递增重命名（报告.txt → 报告2.txt → 报告3.txt…）。</summary>
    private async Task<string> ResolveTargetPathAsync(CancellationToken ct)
    {
        var name = _options.FileName ?? Path.GetFileName(_options.LocalFilePath);
        var target = PcsPath.Join(_options.RemoteDir, name);

        // 上限 1000：目标目录已有大量同名文件时，避免无限次 meta 请求把任务卡死
        for (var n = 2; n <= 1000; n++)
        {
            if (!await ExistsAsync(target, ct).ConfigureAwait(false))
            {
                return target;
            }

            var ext = Path.GetExtension(name);
            var stem = Path.GetFileNameWithoutExtension(name);
            if (string.IsNullOrEmpty(stem))
            {
                // dotfile（如 .gitignore）：GetFileNameWithoutExtension 返回空，退化为整名加序号
                stem = name;
                ext = string.Empty;
            }

            target = PcsPath.Join(_options.RemoteDir, $"{stem}{n}{ext}");
        }

        throw new UploadException(
            $"目标目录同名文件超过上限（1000 个），请先整理远端目录：{_options.RemoteDir}",
            UploadErrorKind.Fatal);
    }

    /// <summary>
    /// 探测远端路径是否存在。
    /// **探测失败不能当作「不存在」**——否则随后会用覆盖语义（<c>rtype=3</c>）上传，
    /// 把远端同名文件静默覆盖掉。这里让异常上抛，由任务层展示真实原因，用户重试即可。
    /// </summary>
    private async Task<bool> ExistsAsync(string path, CancellationToken ct)
    {
        var metas = await _options.Api.GetMetaAsync(new[] { path }, ct).ConfigureAwait(false);
        return metas.Count > 0;
    }

    private async Task<long> GetUkAsync(CancellationToken ct)
    {
        _uk ??= await _options.Api.GetUkAsync(ct).ConfigureAwait(false);
        return _uk.Value;
    }

    /// <summary>locateupload 动态 host；失败降级默认 pcs.baidu.com（协议层空 host 兜底）。</summary>
    private async Task<string?> PickHostAsync(CancellationToken ct)
    {
        try
        {
            var hosts = await _options.Api.LocateUploadHostsAsync(ct).ConfigureAwait(false);
            return hosts.Count == 0 ? null : hosts[Random.Shared.Next(hosts.Count)];
        }
        catch (BaiduApiException)
        {
            return null;
        }
    }

    /// <summary>
    /// 报告进度。countSpeed=false 表示字节来自本地计算/瞬间完成（checksum 读取、续传跳过、秒传），
    /// 只累计进度、不计入网络速度——避免「瞬间批量累计 + 1s 重算周期」误报出 MB/s 级虚高速度。
    /// addedBytes=0 时仅刷新速度/进度显示（由监控心跳调用，让速度按真实时间衰减）。
    /// </summary>
    private void ReportProgress(IProgress<UploadProgress>? progress, long addedBytes, bool countSpeed)
    {
        if (addedBytes > 0)
        {
            if (countSpeed)
            {
                _speedMeter.Add(addedBytes);
            }

            Interlocked.Add(ref _uploadedBytes, addedBytes);
        }

        // 会话失效整体重传/重试重复读时字节会重复报告，clamp 到总大小防止进度超 100%
        var uploaded = Math.Min(Volatile.Read(ref _uploadedBytes), _totalBytes);
        progress?.Report(new UploadProgress
        {
            UploadedBytes = uploaded,
            TotalBytes = _totalBytes,
            SpeedBytesPerSecond = _speedMeter.GetBytesPerSecond(),
        });
    }

    /// <summary>
    /// 时间驱动心跳（对齐下载侧 RunMonitorAsync）：周期刷新进度/速度显示，
    /// 让 SpeedMeter 的累计按真实时间衰减——停顿期间（等待响应、重试退避、慢网络）速度归零、
    /// 进度保持刷新，杜绝「速度冻结在高值 + 进度不动」。
    /// </summary>
    private async Task RunMonitorAsync(IProgress<UploadProgress>? progress, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                await Task.Delay(500, ct).ConfigureAwait(false);
                ReportProgress(progress, addedBytes: 0, countSpeed: false);
            }
        }
        catch (OperationCanceledException)
        {
            // 主流程结束，心跳正常退出
        }
    }
}
