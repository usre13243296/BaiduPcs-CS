using Microsoft.Win32.SafeHandles;

namespace BaiduPcs.Core.Download;

/// <summary>
/// 下载引擎共享上下文：HttpClient、门控、写锁、速度/字节计数与单次下载期状态。
/// </summary>
internal sealed class DownloaderContext
{
    // ======================== 常驻（Downloader 生命周期） ========================

    public required HttpClient Client { get; init; }
    public required string UserAgent { get; init; }
    public string? CookieHeader { get; init; }
    public string? Referer { get; init; }
    public required TimeSpan ReadTimeout { get; init; }
    public required int MaxChunkRetries { get; init; }

    /// <summary>滑动窗口速度统计。</summary>
    public readonly SpeedMeter Speed = new();

    /// <summary>写锁：各 worker 偏移写串行化，减轻磁盘压力（对齐 Go writeMu）。</summary>
    public readonly SemaphoreSlim WriteLock = new(1, 1);

    /// <summary>暂停/恢复门控（初始放行）。</summary>
    public readonly AsyncManualResetEvent PauseGate = new(signaled: true);

    /// <summary>worker 完成信号：唤醒监控循环立即补片。</summary>
    public readonly AsyncManualResetEvent NeedWork = new();

    /// <summary>连接限流：9s 窗口最多 80 次新连接（对齐 Go resetcontroler）。</summary>
    public readonly ConnectionRateLimiter ConnectionLimiter = new();

    /// <summary>已下载字节总数（Interlocked 累加）。</summary>
    public long DownloadedBytes;

    // ======================== 单次下载期（DownloadWithUrlAsync 设置） ========================

    public volatile string? CurrentUrl;
    public SafeFileHandle? File;
    public string? CheckpointPath;
    public long TotalSize;
    public long BlockSize;
    public RangeGenerator? Gen;
    public List<ChunkWorker>? Workers;
    public volatile bool Stopping;

    /// <summary>按绝对偏移写入 .part（写锁串行化）。</summary>
    public async Task WriteAtAsync(ReadOnlyMemory<byte> buffer, long offset, CancellationToken ct)
    {
        await WriteLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await RandomAccess.WriteAsync(File!, buffer, offset, ct).ConfigureAwait(false);
        }
        finally
        {
            WriteLock.Release();
        }
    }

    /// <summary>持久化断点（对齐 Go monitor 990ms tick）。仅下载进行中有效，写入失败不中断下载。</summary>
    public void SaveCheckpoint()
    {
        if (Gen is null || Workers is null || File is null || string.IsNullOrEmpty(CheckpointPath))
        {
            return;
        }

        try
        {
            var cp = new DownloadCheckpoint
            {
                TotalSize = TotalSize,
                BlockSize = BlockSize,
                GenBegin = Gen.Begin,
                Ranges = Workers.Where(w => w.HasRange).Select(w => w.RemainingRange).ToList(),
            };
            cp.Save(CheckpointPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // 断点尽力而为：保存失败不中断下载
        }
    }
}
