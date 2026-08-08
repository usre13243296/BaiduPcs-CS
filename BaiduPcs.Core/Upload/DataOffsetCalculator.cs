using System.Security.Cryptography;
using System.Text;

namespace BaiduPcs.Core.Upload;

/// <summary>
/// 秒传防滥用随机片段计算（对齐 Go pcsupload/utils.go 的 creaetDataOffset）：
/// offset = md5("{uk}{contentMd5}{unix秒}") 前 8 位 hex 取模 (fileSize - subSize + 1)；
/// data_content 为 offset 处 4KB 内容 base64 去尾部 '='（对齐 upload_task_unit.go GetSliceDataContent）。
/// </summary>
public static class DataOffsetCalculator
{
    /// <summary>秒传校验子片段长度（4KB，对齐 Go DefaultContentSize）。</summary>
    public const long DefaultContentSize = 4 * 1024;

    /// <summary>计算 data_offset。fileSize - subSize + 1 ≤ 1 时返回 0（小文件无随机空间）。</summary>
    public static long CalculateDataOffset(string contentMd5, long uk, long dataTime, long fileSize, long subSize = DefaultContentSize)
    {
        var sumStr = $"{uk}{contentMd5}{dataTime}";
        var hex = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(sumStr)));
        var rawOffset = Convert.ToInt64(hex[..8], 16);

        if (fileSize - subSize + 1 <= 1)
        {
            return 0;
        }

        return rawOffset % (fileSize - subSize + 1);
    }

    /// <summary>从文件 offset 处读取 length 字节（不足则取余量）。</summary>
    public static byte[] ReadDataContent(string filePath, long offset, long length = DefaultContentSize)
    {
        using var fs = File.OpenRead(filePath);
        fs.Seek(offset, SeekOrigin.Begin);

        var buffer = new byte[length];
        var read = fs.Read(buffer, 0, (int)length);
        return buffer.AsSpan(0, read).ToArray();
    }

    /// <summary>标准 base64 并去掉尾部 '='（对齐 Go strings.TrimRight(..., "=")）。</summary>
    public static string ToBase64WithoutPadding(byte[] data)
        => Convert.ToBase64String(data).TrimEnd('=');
}
