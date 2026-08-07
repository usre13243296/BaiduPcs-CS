using System.Text.Json.Serialization;

namespace BaiduPcs.Core.Models;

/// <summary>
/// 网盘文件或目录的元信息（对应 Go 端 FileDirectory / fdJSON）。
/// </summary>
public sealed class FileEntry
{
    /// <summary>fs_id</summary>
    [JsonPropertyName("fs_id")]
    public long FsId { get; set; }

    /// <summary>app_id</summary>
    [JsonPropertyName("app_id")]
    public long AppId { get; set; }

    /// <summary>网盘路径</summary>
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    /// <summary>文件名或目录名（server_filename）</summary>
    [JsonPropertyName("server_filename")]
    public string Filename { get; set; } = string.Empty;

    /// <summary>创建时间（Unix 秒）</summary>
    [JsonPropertyName("ctime")]
    public long Ctime { get; set; }

    /// <summary>修改时间（Unix 秒）</summary>
    [JsonPropertyName("mtime")]
    public long Mtime { get; set; }

    /// <summary>md5（可能为加密形式，需经 DecryptMD5 还原）</summary>
    [JsonPropertyName("md5")]
    public string Md5 { get; set; } = string.Empty;

    /// <summary>分片 md5 列表</summary>
    [JsonPropertyName("block_list")]
    public List<string>? BlockList { get; set; }

    /// <summary>文件大小（目录为 0）</summary>
    [JsonPropertyName("size")]
    public long Size { get; set; }

    /// <summary>是否为目录（0/1）</summary>
    [JsonPropertyName("isdir")]
    public int IsdirInt { get; set; }

    /// <summary>是否含有子目录（仅对目录有效，0/1）</summary>
    [JsonPropertyName("ifhassubdir")]
    public int IfhassubdirInt { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsDir => IsdirInt == 1;

    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasSubDir => IfhassubdirInt == 1;
}
