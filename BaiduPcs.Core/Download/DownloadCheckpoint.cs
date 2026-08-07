using System.Text.Json;

namespace BaiduPcs.Core.Download;

/// <summary>
/// 下载断点元数据（对齐 Go transfer.proto 的 DownloadInstanceInfoExport，以 JSON 落盘）。
/// 数据本身写入 name.part，本类记录各分片剩余范围与生成器进度，恢复时据此续传。
/// </summary>
public sealed class DownloadCheckpoint
{
    /// <summary>文件总大小（字节）。</summary>
    public long TotalSize { get; set; }

    /// <summary>分片大小（字节）。</summary>
    public long BlockSize { get; set; }

    /// <summary>Range 生成器已分配到的位置。</summary>
    public long GenBegin { get; set; }

    /// <summary>各 worker 当前剩余（未完成）的分片范围。</summary>
    public List<DownloadRange> Ranges { get; set; } = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// 读取断点文件。文件不存在或内容损坏（JSON 非法 / TotalSize &lt;= 0）返回 null。
    /// </summary>
    public static DownloadCheckpoint? TryLoad(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var cp = JsonSerializer.Deserialize<DownloadCheckpoint>(File.ReadAllText(path), JsonOptions);
            return cp is { TotalSize: > 0 } ? cp : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>原子保存：写临时文件后覆盖替换，避免写入中断损坏断点。</summary>
    public void Save(string path)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(tmp, path, overwrite: true);
    }
}
