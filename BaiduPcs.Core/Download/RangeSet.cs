namespace BaiduPcs.Core.Download;

/// <summary>
/// 下载分片范围。半开区间 [Begin, End)，End 为"下一个待下载字节"（绝对偏移）。
/// 对应 Go 端 requester/transfer 的 Range{Begin, End}。
/// </summary>
public readonly record struct DownloadRange(long Begin, long End)
{
    /// <summary>剩余字节数。</summary>
    public long Length => End - Begin;

    /// <summary>是否为空（已下载完）。</summary>
    public bool IsEmpty => End <= Begin;

    /// <summary>以新起点构造剩余部分 [newBegin, End)。</summary>
    public DownloadRange WithBegin(long newBegin) => new(newBegin, End);
}

/// <summary>
/// 分片大小档位（对齐 Go SelectBlockSizeAndInitRangeGen 的 BlockSizeList）。
/// 该选择被 Downloader 使用，独立成类便于单测。
/// </summary>
public static class BlockSizes
{
    /// <summary>单线程最小数据量（对齐 Go MinParallelSize，用于并发数上限约束）。</summary>
    public const long MinParallelSize = 256 * 1024;

    /// <summary>按文件大小选择分片大小。</summary>
    public static long Select(long totalSize)
    {
        const long mb = 1024 * 1024;
        return totalSize switch
        {
            < 5 * mb => 256 * 1024,
            < 10 * mb => 512 * 1024,
            < 15 * mb => 1 * mb,
            _ => 4 * mb,
        };
    }
}

/// <summary>
/// 线程安全的 Range 生成器：按 blockSize 顺序切块 [begin, begin+blockSize)，begin 原子推进。
/// 对应 Go 端 requester/transfer/rangelist.go 的 RangeListGen.GenRange。
/// </summary>
public sealed class RangeGenerator
{
    private readonly long _totalSize;
    private readonly long _blockSize;
    private long _begin;

    public RangeGenerator(long totalSize, long blockSize, long begin = 0)
    {
        if (totalSize < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(totalSize));
        }

        if (blockSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(blockSize));
        }

        _totalSize = totalSize;
        _blockSize = blockSize;
        _begin = Math.Min(begin, totalSize);
    }

    /// <summary>已分配到的位置（断点持久化用）。</summary>
    public long Begin => Volatile.Read(ref _begin);

    /// <summary>是否已分配完全部数据。</summary>
    public bool IsDone => Volatile.Read(ref _begin) >= _totalSize;

    /// <summary>取下一块 range；无剩余返回 false。</summary>
    public bool TryGenerate(out DownloadRange range)
    {
        while (true)
        {
            var b = Volatile.Read(ref _begin);
            if (b >= _totalSize)
            {
                range = default;
                return false;
            }

            var e = Math.Min(_totalSize, b + _blockSize);
            if (Interlocked.CompareExchange(ref _begin, e, b) == b)
            {
                range = new DownloadRange(b, e);
                return true;
            }
        }
    }
}
