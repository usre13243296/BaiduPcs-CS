using System.Diagnostics;

namespace BaiduPcs.Core.Download;

/// <summary>
/// 1 秒滑动窗口速度统计。窗口内字节 / 窗口实际时长，平滑且无跳变。
/// 对应 Go 端 requester/rio/speeds 的 Speeds。
/// </summary>
public sealed class SpeedMeter
{
    private readonly object _lock = new();
    private readonly Queue<(long Timestamp, long Bytes)> _samples = new();
    private long _windowBytes;

    private const long WindowTicks = TimeSpan.TicksPerSecond;

    /// <summary>累计一段已下载字节。</summary>
    public void Add(long bytes)
    {
        if (bytes <= 0)
        {
            return;
        }

        lock (_lock)
        {
            var now = Stopwatch.GetTimestamp();
            _samples.Enqueue((now, bytes));
            _windowBytes += bytes;
            Trim(now);
        }
    }

    /// <summary>当前速度（字节/秒）。无样本返回 0。</summary>
    public double GetBytesPerSecond()
    {
        lock (_lock)
        {
            var now = Stopwatch.GetTimestamp();
            Trim(now);
            if (_samples.Count == 0)
            {
                return 0;
            }

            var elapsed = now - _samples.Peek().Timestamp;
            if (elapsed <= 0)
            {
                return 0;
            }

            return _windowBytes * (double)Stopwatch.Frequency / elapsed;
        }
    }

    /// <summary>移除窗口外的样本。</summary>
    private void Trim(long now)
    {
        while (_samples.Count > 0 && now - _samples.Peek().Timestamp > WindowTicks)
        {
            _windowBytes -= _samples.Dequeue().Bytes;
        }
    }
}
