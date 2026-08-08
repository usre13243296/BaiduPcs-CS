using System.Security.Cryptography;
using System.Text;
using BaiduPcs.Core.Api;
using BaiduPcs.Core.Upload;

namespace BaiduPcs.Core.Tests;

public class UploadChecksumTests
{
    private static string Md5Hex(byte[] data) => Convert.ToHexStringLower(MD5.HashData(data));

    private static string MakeTempFile(byte[] content)
    {
        var path = Path.Combine(Path.GetTempPath(), "upck_" + Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllBytes(path, content);
        return path;
    }

    [Fact]
    public async Task ComputeAsync_TinyFile_AllThreeEqual()
    {
        var content = Encoding.UTF8.GetBytes("abc");
        var path = MakeTempFile(content);
        try
        {
            var checksum = await UploadChecksum.ComputeAsync(path, blockSize: 4 * 1024 * 1024);

            // 全文件 md5（已知向量）
            Assert.Equal("900150983cd24fb0d6963f7d28e17f72", checksum.ContentMd5);
            // 文件 < 256KB：slice-md5 == content-md5；单块：块 md5 == content-md5
            Assert.Equal(checksum.ContentMd5, checksum.SliceMd5);
            Assert.Equal(new[] { checksum.ContentMd5 }, checksum.BlockMd5s);
            Assert.Equal(3, checksum.Length);
            Assert.Equal(4 * 1024 * 1024, checksum.BlockSize);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ComputeAsync_SliceMd5_TakesFirst256KB()
    {
        // 300KB 内容：slice-md5 = 前 256KB 的 md5 ≠ 全文件 md5
        var content = new byte[300 * 1024];
        for (var i = 0; i < content.Length; i++)
        {
            content[i] = (byte)((i * 31 + i / 251) % 251);
        }

        var path = MakeTempFile(content);
        try
        {
            var checksum = await UploadChecksum.ComputeAsync(path, blockSize: 4 * 1024 * 1024);

            Assert.Equal(Md5Hex(content[..(256 * 1024)]), checksum.SliceMd5);
            Assert.NotEqual(checksum.SliceMd5, checksum.ContentMd5);
            Assert.Equal(Md5Hex(content), checksum.ContentMd5);
            Assert.Single(checksum.BlockMd5s); // 300KB < 4MB：单块
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ComputeAsync_CrossBlockBoundary_SplitsCorrectly()
    {
        // 4MB + 1 字节：两块，块 0 = 前 4MB，块 1 = 最后 1 字节
        var blockSize = 4 * 1024 * 1024L;
        var content = new byte[blockSize + 1];
        for (var i = 0; i < content.Length; i++)
        {
            content[i] = (byte)((i * 7 + 13) % 251);
        }

        var path = MakeTempFile(content);
        try
        {
            var checksum = await UploadChecksum.ComputeAsync(path, blockSize);

            Assert.Equal(2, checksum.BlockMd5s.Count);
            Assert.Equal(Md5Hex(content[..(int)blockSize]), checksum.BlockMd5s[0]);
            Assert.Equal(Md5Hex(content[(int)blockSize..]), checksum.BlockMd5s[1]);
            Assert.Equal(Md5Hex(content), checksum.ContentMd5);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ComputeAsync_MultiBlock_OrderedBySeq()
    {
        // 9MB：3 块（4MB/4MB/1MB）
        var blockSize = 4 * 1024 * 1024L;
        var content = new byte[9 * 1024 * 1024];
        for (var i = 0; i < content.Length; i++)
        {
            content[i] = (byte)(i % 251);
        }

        var path = MakeTempFile(content);
        try
        {
            var checksum = await UploadChecksum.ComputeAsync(path, blockSize);

            Assert.Equal(3, checksum.BlockMd5s.Count);
            Assert.Equal(Md5Hex(content[..(4 * 1024 * 1024)]), checksum.BlockMd5s[0]);
            Assert.Equal(Md5Hex(content[(4 * 1024 * 1024)..(8 * 1024 * 1024)]), checksum.BlockMd5s[1]);
            Assert.Equal(Md5Hex(content[(8 * 1024 * 1024)..]), checksum.BlockMd5s[2]);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public class UploadBlockSizeTests
{
    [Theory]
    [InlineData(0, BaiduPcsApiClient.MinUploadBlockSize)]
    [InlineData(4 * 1024 * 1024, BaiduPcsApiClient.MinUploadBlockSize)]
    [InlineData(8L * 1024 * 1024 * 1024 - 1, BaiduPcsApiClient.MinUploadBlockSize)]
    [InlineData(8L * 1024 * 1024 * 1024, BaiduPcsApiClient.MiddleUploadBlockSize)]
    [InlineData(32L * 1024 * 1024 * 1024 - 1, BaiduPcsApiClient.MiddleUploadBlockSize)]
    [InlineData(32L * 1024 * 1024 * 1024, BaiduPcsApiClient.MaxUploadBlockSize)]
    [InlineData(128L * 1024 * 1024 * 1024, BaiduPcsApiClient.MaxUploadBlockSize)]
    public void GetBlockSize_SelectsByFileSize(long fileSize, long expected)
    {
        Assert.Equal(expected, Uploader.GetBlockSize(fileSize));
    }
}

public class DataOffsetCalculatorTests
{
    // 期望值来源（node 独立计算）：
    //   sumStr = "777abc1231600000000" → md5 hex 前 8 位 "ab152393" → raw = 2870289299
    //   fileSize=1MB → 2870289299 % (1048576-4096+1) = 55511
    //   fileSize=5000 → 2870289299 % (5000-4096+1) = 349
    private const string ContentMd5 = "abc123";
    private const long Uk = 777;
    private const long DataTime = 1600000000;

    [Fact]
    public void CalculateDataOffset_LargeFile_Modulo()
    {
        Assert.Equal(55511, DataOffsetCalculator.CalculateDataOffset(ContentMd5, Uk, DataTime, fileSize: 1024 * 1024));
    }

    [Fact]
    public void CalculateDataOffset_SmallFile_Modulo()
    {
        Assert.Equal(349, DataOffsetCalculator.CalculateDataOffset(ContentMd5, Uk, DataTime, fileSize: 5000));
    }

    [Fact]
    public void CalculateDataOffset_TinyFile_Zero()
    {
        // fileSize - subSize + 1 <= 1 → 0
        Assert.Equal(0, DataOffsetCalculator.CalculateDataOffset(ContentMd5, Uk, DataTime, fileSize: 100));
        Assert.Equal(0, DataOffsetCalculator.CalculateDataOffset(ContentMd5, Uk, DataTime, fileSize: 4096));
    }

    [Fact]
    public void ToBase64WithoutPadding_StripsPadding()
    {
        Assert.Equal("AQI", DataOffsetCalculator.ToBase64WithoutPadding(new byte[] { 1, 2 }));
        Assert.Equal("AQID", DataOffsetCalculator.ToBase64WithoutPadding(new byte[] { 1, 2, 3 }));
        Assert.Equal("YWJj", DataOffsetCalculator.ToBase64WithoutPadding(Encoding.UTF8.GetBytes("abc")));
    }

    [Fact]
    public void ReadDataContent_ReadsFromOffset()
    {
        var path = Path.Combine(Path.GetTempPath(), "updo_" + Guid.NewGuid().ToString("N") + ".bin");
        try
        {
            var content = new byte[1024];
            for (var i = 0; i < content.Length; i++)
            {
                content[i] = (byte)(i % 251);
            }

            File.WriteAllBytes(path, content);

            var slice = DataOffsetCalculator.ReadDataContent(path, 500, length: 4096);
            Assert.Equal(1024 - 500, slice.Length); // 不足 length 时取余量
            Assert.Equal(content[500..], slice);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
