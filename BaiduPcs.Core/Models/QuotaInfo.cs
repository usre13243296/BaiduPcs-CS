using System.Text.Json.Serialization;

namespace BaiduPcs.Core.Models;

/// <summary>当前用户空间配额信息（对应 Go 端 quotaInfo）。</summary>
public sealed class QuotaInfo
{
    [JsonPropertyName("quota")]
    public long Quota { get; set; }

    [JsonPropertyName("used")]
    public long Used { get; set; }

    /// <summary>剩余空间</summary>
    public long Free => Quota - Used;
}
