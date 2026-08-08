using System.Security.Cryptography;

namespace BaiduPcs.Core.Upload;

/// <summary>
/// 上传校验和：单趟流式计算三份 MD5（对齐 Go pcsutil/checksum 的 Sum + CalculateChunkedSum）：
/// - ContentMd5：全文件 MD5（秒传 content-md5）
/// - SliceMd5：文件前 256KB 的 MD5（文件不足 256KB 时为全文件；秒传 slice-md5）
/// - BlockMd5s：按分片大小的逐块 MD5（superfile2 create 的 block_list，与分片一一对应）
/// </summary>
public sealed class UploadChecksum
{
    private const int BufferSize = 1024 * 1024;

    private UploadChecksum(long length, long blockSize, string contentMd5, string sliceMd5, IReadOnlyList<string> blockMd5s)
    {
        Length = length;
        BlockSize = blockSize;
        ContentMd5 = contentMd5;
        SliceMd5 = sliceMd5;
        BlockMd5s = blockMd5s;
    }

    public long Length { get; }

    /// <summary>分片大小（计算 block_list 时使用）。</summary>
    public long BlockSize { get; }

    /// <summary>全文件 MD5（小写 hex）。</summary>
    public string ContentMd5 { get; }

    /// <summary>文件前 256KB 的 MD5（小写 hex）。</summary>
    public string SliceMd5 { get; }

    /// <summary>按分片顺序的逐块 MD5（小写 hex，与 partseq 一一对应）。</summary>
    public IReadOnlyList<string> BlockMd5s { get; }

    /// <summary>
    /// 单趟流式计算：读文件一遍，同时累计全文件/前缀/逐块三份 MD5。
    /// progress 可选：每读一个缓冲回调已读字节数（调用方用于进度显示；注意这是本地计算，
    /// 不应计入网络速度）。
    /// </summary>
    public static async Task<UploadChecksum> ComputeAsync(
        string filePath, long blockSize, IProgress<long>? progress = null, CancellationToken ct = default)
    {
        var length = new FileInfo(filePath).Length;
        var blockCount = (int)((length + blockSize - 1) / blockSize);

        var contentHash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        var sliceHash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        var blockHashes = new List<IncrementalHash>(blockCount);
        for (var i = 0; i < blockCount; i++)
        {
            blockHashes.Add(IncrementalHash.CreateHash(HashAlgorithmName.MD5));
        }

        var sliceEnd = Math.Min(256 * 1024, length);
        var buffer = new byte[BufferSize];

        using var fs = new FileStream(
            filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);

        long filePos = 0;
        while (filePos < length)
        {
            var read = await fs.ReadAsync(
                buffer.AsMemory(0, (int)Math.Min(buffer.Length, length - filePos)), ct).ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            // 全文件 MD5
            contentHash.AppendData(buffer, 0, read);

            // 前 256KB 的 MD5（不足则全文件）
            if (filePos < sliceEnd)
            {
                var take = (int)Math.Min(read, sliceEnd - filePos);
                sliceHash.AppendData(buffer, 0, take);
            }

            // 逐块 MD5：缓冲可能跨块边界，按块切分喂给对应块的哈希
            var span = buffer.AsSpan(0, read);
            var offset = 0;
            while (offset < read)
            {
                var pos = filePos + offset;
                var blockIndex = (int)(pos / blockSize);
                var blockEnd = (blockIndex + 1) * blockSize;
                var take = (int)Math.Min(read - offset, blockEnd - pos);
                blockHashes[blockIndex].AppendData(span.Slice(offset, take));
                offset += take;
            }

            filePos += read;
            progress?.Report(read);
        }

        try
        {
            return new UploadChecksum(
                length,
                blockSize,
                Convert.ToHexStringLower(contentHash.GetHashAndReset()),
                Convert.ToHexStringLower(sliceHash.GetHashAndReset()),
                blockHashes.Select(h => Convert.ToHexStringLower(h.GetHashAndReset())).ToList());
        }
        finally
        {
            foreach (var h in blockHashes)
            {
                h.Dispose();
            }

            contentHash.Dispose();
            sliceHash.Dispose();
        }
    }
}
