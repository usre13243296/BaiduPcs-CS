using System.Diagnostics;

namespace BaiduPcs.Core.Download;

/// <summary>
/// 速度统计（周期采样模型）：每次计算时若距上次计算 ≥ 1s，用「周期内累计字节 / 实际经过时间」
/// 重算速度并清零累计；间隔不足 1s 时返回上次速度。
/// 相比 1s 滑动窗口：样本稀疏时（如上传按分片/流块报告）不会出现
/// "单样本 + 微秒跨度 → 瞬时速度爆炸（TB/s）" 的缺陷，慢速/快速场景均准确。
/// </summary>
public sealed class SpeedMeter
{
    private readonly object _lock = new();

    // 1s 周期 = Stopwatch.Frequency 个 tick（不能用 TimeSpan.TicksPerSecond：
    // 后者只在 Frequency==10MHz 的平台才等于 1s，Linux 等平台 Frequency 为 1e9 会导致闸门坍缩）
    private readonly long _periodTicks = Stopwatch.Frequency;

    private long _pendingBytes;
    private long _lastCalcTimestamp = Stopwatch.GetTimestamp();
    private double _lastSpeed;

    /// <summary>累计一段已传输字节（线程安全）。</summary>
    public void Add(long bytes)
    {
        if (bytes <= 0)
        {
            return;
        }

        lock (_lock)
        {
            _pendingBytes += bytes;
        }
    }

    /// <summary>当前速度（字节/秒）。距上次计算不足 1s 时返回上次速度；尚无周期数据返回 0。</summary>
    public double GetBytesPerSecond()
    {
        lock (_lock)
        {
            var now = Stopwatch.GetTimestamp();
            var elapsed = now - _lastCalcTimestamp;
            if (elapsed < _periodTicks)
            {
                return _lastSpeed;
            }

            var speed = _pendingBytes * (double)Stopwatch.Frequency / elapsed;
            _pendingBytes = 0;
            _lastCalcTimestamp = now;
            _lastSpeed = speed;
            return speed;
        }
    }
}
