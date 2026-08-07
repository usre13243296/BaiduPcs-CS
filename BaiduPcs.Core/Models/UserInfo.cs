using System.Text.Json.Serialization;

namespace BaiduPcs.Core.Models;

/// <summary>百度用户信息（对应 Go 端 BaiduBase / userInfoJSON.records）。</summary>
public sealed class UserInfo
{
    /// <summary>百度 id 对应的 uid</summary>
    [JsonPropertyName("uk")]
    public long Uk { get; set; }

    /// <summary>网名</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>真实 ID（部分接口返回）</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;
}
