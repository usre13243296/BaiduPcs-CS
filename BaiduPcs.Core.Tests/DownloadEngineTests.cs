using System.Net;
using BaiduPcs.Core.Download;

namespace BaiduPcs.Core.Tests;

public class RangeGeneratorTests
{
    [Fact]
    public void Generate_SequentialChunks_NoOverlapNoGap()
    {
        var gen = new RangeGenerator(1000, 256);
        var ranges = new List<DownloadRange>();
        while (gen.TryGenerate(out var r))
        {
            ranges.Add(r);
        }

        Assert.Equal(4, ranges.Count);
        Assert.Equal(new DownloadRange(0, 256), ranges[0]);
        Assert.Equal(new DownloadRange(256, 512), ranges[1]);
        Assert.Equal(new DownloadRange(512, 768), ranges[2]);
        Assert.Equal(new DownloadRange(768, 1000), ranges[3]); // 余数片
        Assert.True(gen.IsDone);
        Assert.Equal(1000, gen.Begin);
    }

    [Fact]
    public void Generate_ExactMultiple_AllFullBlocks()
    {
        var gen = new RangeGenerator(1024, 256);
        var count = 0;
        while (gen.TryGenerate(out var r))
        {
            Assert.Equal(256, r.Length);
            count++;
        }

        Assert.Equal(4, count);
    }

    [Fact]
    public void Generate_EmptyFile_Nothing()
    {
        var gen = new RangeGenerator(0, 256);
        Assert.False(gen.TryGenerate(out _));
        Assert.True(gen.IsDone);
    }

    [Fact]
    public void Generate_ResumeFromBegin_ContinuesAtBegin()
    {
        var gen = new RangeGenerator(1000, 256, begin: 512);
        Assert.True(gen.TryGenerate(out var r));
        Assert.Equal(new DownloadRange(512, 768), r);
        Assert.Equal(768, gen.Begin);
    }

    [Fact]
    public async Task Generate_Concurrent_CoversEntireFileExactlyOnce()
    {
        // 模拟 8 个 worker 并发取片：所有片拼起来 == 完整 [0, total)
        const long total = 10_000_000;
        var gen = new RangeGenerator(total, 4 * 1024 * 1024);
        var ranges = new List<DownloadRange>();
        var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            while (gen.TryGenerate(out var r))
            {
                lock (ranges)
                {
                    ranges.Add(r);
                }
            }
        }));

        await Task.WhenAll(tasks);

        var sorted = ranges.OrderBy(r => r.Begin).ToList();
        Assert.Equal(3, sorted.Count);
        Assert.Equal(0, sorted[0].Begin);
        Assert.Equal(sorted[0].End, sorted[1].Begin);
        Assert.Equal(sorted[1].End, sorted[2].Begin);
        Assert.Equal(total, sorted[^1].End);
    }
}

public class BlockSizesTests
{
    [Theory]
    [InlineData(1, 256 * 1024)]
    [InlineData(5 * 1024 * 1024 - 1, 256 * 1024)]
    [InlineData(5 * 1024 * 1024, 512 * 1024)]
    [InlineData(10 * 1024 * 1024 - 1, 512 * 1024)]
    [InlineData(10 * 1024 * 1024, 1024 * 1024)]
    [InlineData(15 * 1024 * 1024 - 1, 1024 * 1024)]
    [InlineData(15 * 1024 * 1024, 4 * 1024 * 1024)]
    [InlineData(1024 * 1024 * 1024, 4 * 1024 * 1024)]
    public void Select_SizeTiers(long totalSize, long expected)
    {
        Assert.Equal(expected, BlockSizes.Select(totalSize));
    }
}

public class SpeedMeterTests
{
    [Fact]
    public void Empty_ReturnsZero()
    {
        Assert.Equal(0, new SpeedMeter().GetBytesPerSecond());
    }

    [Fact]
    public void AddBytes_WithinFirstSecond_ReturnsPreviousSpeed()
    {
        var meter = new SpeedMeter();
        meter.Add(2000);
        Thread.Sleep(200);
        meter.Add(2000);
        // 首个周期未满 1s：返回上次速度（初始 0），不出现"单样本 + 微秒跨度"的瞬时爆炸
        Assert.Equal(0, meter.GetBytesPerSecond());
    }

    [Fact]
    public void AddBytes_AfterOneSecond_ReturnsRate_ThenClears()
    {
        var meter = new SpeedMeter();
        meter.Add(2000);
        Thread.Sleep(200);
        meter.Add(2000);
        Thread.Sleep(900); // 累计 > 1s
        var rate = meter.GetBytesPerSecond();
        // 4000 字节 / 1.1s ≈ 3600 B/s，允许 ±60% 抖动（Thread.Sleep 不可靠）
        Assert.InRange(rate, 1500, 12000);
        // 重算后周期字节清零：下一个周期（无新字节）速度为 0
        Thread.Sleep(1100);
        Assert.Equal(0, meter.GetBytesPerSecond());
    }

    [Fact]
    public void SparseSamples_DoNotExplode()
    {
        // 稀疏样本（上传场景：每 2s 完成一个 4MB 分片）→ 速度 ≈ 4MB/s，绝不出现 TB/s 级数值
        var meter = new SpeedMeter();
        meter.Add(4 * 1024 * 1024);
        Thread.Sleep(2100);
        meter.Add(4 * 1024 * 1024);
        var rate = meter.GetBytesPerSecond();
        // 8MB / 2.1s ≈ 4MB/s，允许 ±50% 抖动
        Assert.InRange(rate, 2 * 1024 * 1024, 20 * 1024 * 1024);
        // 硬性上限：绝不超过 1TB/s（旧滑动窗口模型单样本时可达 TB/s）
        Assert.True(rate < 1024L * 1024 * 1024 * 1024, $"速度异常爆炸: {rate} B/s");
    }
}

public class DownloadErrorClassifierTests
{
    [Theory]
    [InlineData(200, DownloadErrorKind.None)]
    [InlineData(206, DownloadErrorKind.None)]
    [InlineData(403, DownloadErrorKind.Fatal)]
    [InlineData(404, DownloadErrorKind.Fatal)]
    [InlineData(416, DownloadErrorKind.Fatal)]
    [InlineData(429, DownloadErrorKind.RateLimited)]
    [InlineData(509, DownloadErrorKind.RateLimited)]
    [InlineData(500, DownloadErrorKind.Network)]
    [InlineData(502, DownloadErrorKind.Network)]
    [InlineData(406, DownloadErrorKind.Network)]
    public void Classify_StatusCodes(int status, DownloadErrorKind expected)
    {
        Assert.Equal(expected, DownloadErrorClassifier.Classify((HttpStatusCode)status));
    }
}

public class DownloadRetryPolicyTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    [InlineData(3, 6)]
    [InlineData(10, 6)]
    public void NextWait_LinearBackoffCapped(int retryCount, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), DownloadRetryPolicy.NextWait(retryCount));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(9, 4)]
    public void RateLimitWait_ExponentialCapped(int attempt, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), DownloadRetryPolicy.RateLimitWait(attempt));
    }
}

public class ConnectionRateLimiterTests
{
    [Fact]
    public void Acquire_RespectsWindowLimit()
    {
        var limiter = new ConnectionRateLimiter(max: 2, window: TimeSpan.FromMilliseconds(200));
        Assert.True(limiter.TryAcquire());
        Assert.True(limiter.TryAcquire());
        Assert.False(limiter.TryAcquire()); // 窗口内已满
    }

    [Fact]
    public void Acquire_ExpiredWindow_Releases()
    {
        var limiter = new ConnectionRateLimiter(max: 2, window: TimeSpan.FromMilliseconds(100));
        Assert.True(limiter.TryAcquire());
        Assert.True(limiter.TryAcquire());
        Assert.False(limiter.TryAcquire());
        Thread.Sleep(150);
        Assert.True(limiter.TryAcquire()); // 窗口过期后恢复
    }
}

public class DownloadCheckpointTests
{
    [Fact]
    public void SaveLoad_RoundTrip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cp-{Guid.NewGuid():N}.json");
        try
        {
            var cp = new DownloadCheckpoint
            {
                TotalSize = 123456789,
                BlockSize = 4 * 1024 * 1024,
                GenBegin = 8 * 1024 * 1024,
                Ranges =
                {
                    new DownloadRange(1_000, 2_000),
                    new DownloadRange(8_000_000, 12_000_000),
                },
            };
            cp.Save(path);

            var loaded = DownloadCheckpoint.TryLoad(path);
            Assert.NotNull(loaded);
            Assert.Equal(123456789, loaded!.TotalSize);
            Assert.Equal(4 * 1024 * 1024, loaded.BlockSize);
            Assert.Equal(8 * 1024 * 1024, loaded.GenBegin);
            Assert.Equal(2, loaded.Ranges.Count);
            Assert.Equal(new DownloadRange(1_000, 2_000), loaded.Ranges[0]);
            Assert.Equal(new DownloadRange(8_000_000, 12_000_000), loaded.Ranges[1]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TryLoad_MissingFile_ReturnsNull()
    {
        Assert.Null(DownloadCheckpoint.TryLoad(Path.Combine(Path.GetTempPath(), "missing-" + Guid.NewGuid().ToString("N") + ".json")));
    }

    [Fact]
    public void TryLoad_CorruptedJson_ReturnsNull()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cp-bad-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{ not json !!!");
            Assert.Null(DownloadCheckpoint.TryLoad(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TryLoad_InvalidTotalSize_ReturnsNull()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cp-zero-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """{"TotalSize":0,"BlockSize":262144,"GenBegin":0,"Ranges":[]}""");
            Assert.Null(DownloadCheckpoint.TryLoad(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
