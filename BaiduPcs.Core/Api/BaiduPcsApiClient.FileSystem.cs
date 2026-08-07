using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BaiduPcs.Core.Models;

namespace BaiduPcs.Core.Api;

/// <summary>排序字段（对应 Go 端 OrderBy）。</summary>
public enum OrderBy
{
    Name,
    Time,
    Size,
}

/// <summary>排序方向（对应 Go 端 Order）。</summary>
public enum OrderDirection
{
    Asc,
    Desc,
}

public sealed partial class BaiduPcsApiClient
{
    private sealed class FileListEnvelope
    {
        [JsonPropertyName("list")]
        public List<FileEntry>? List { get; set; }
    }

    private sealed class PathItem
    {
        [JsonPropertyName("path")]
        public string? Path { get; set; }
    }

    private sealed class PathListEnvelope
    {
        [JsonPropertyName("list")]
        public List<PathItem>? List { get; set; }
    }

    private sealed class CopyMoveItem
    {
        [JsonPropertyName("from")]
        public string? From { get; set; }

        [JsonPropertyName("to")]
        public string? To { get; set; }
    }

    private sealed class CopyMoveEnvelope
    {
        [JsonPropertyName("list")]
        public List<CopyMoveItem>? List { get; set; }
    }

    private static string OrderByToString(OrderBy by) => by switch
    {
        OrderBy.Time => "time",
        OrderBy.Size => "size",
        _ => "name",
    };

    private static string OrderDirectionToString(OrderDirection order) => order switch
    {
        OrderDirection.Desc => "desc",
        _ => "asc",
    };

    /// <summary>发送请求并仅校验响应错误，不要求返回值。</summary>
    private async Task SendAndCheckAsync(
        ApiRequestType requestType,
        string operation,
        HttpMethod method,
        Uri url,
        HttpContent? content = null,
        IReadOnlyDictionary<string, string>? headers = null,
        CancellationToken ct = default)
    {
        var body = await SendRawAsync(requestType, operation, method, url, content, headers, ct).ConfigureAwait(false);
        try
        {
            using var doc = JsonDocument.Parse(body);
            CheckError(requestType, operation, doc.RootElement);
        }
        catch (JsonException ex)
        {
            throw BaiduApiException.JsonParseError(operation, ex);
        }
    }

    /// <summary>获取当前用户空间配额信息。GET /rest/2.0/pcs/quota?method=info</summary>
    public Task<QuotaInfo> GetQuotaInfoAsync(CancellationToken ct = default)
    {
        var url = GeneratePcsUrl("quota", "info");
        return SendJsonAsync<QuotaInfo>(ApiRequestType.Pcs, OperationQuotaInfo, HttpMethod.Get, url, ct: ct);
    }

    /// <summary>获取目录下的文件列表。GET /rest/2.0/pcs/file?method=list</summary>
    public async Task<List<FileEntry>> ListFilesAsync(
        string path,
        OrderBy by = OrderBy.Name,
        OrderDirection order = OrderDirection.Asc,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(path))
        {
            path = ApiConstants.PathSeparator;
        }

        var url = GeneratePcsUrl("file", "list", new Dictionary<string, string>
        {
            ["path"] = path,
            ["by"] = OrderByToString(by),
            ["order"] = OrderDirectionToString(order),
        });

        var envelope = await SendJsonAsync<FileListEnvelope>(
            ApiRequestType.Pcs,
            OperationFilesDirectoriesList,
            HttpMethod.Get,
            url,
            ct: ct).ConfigureAwait(false);

        return envelope.List ?? new List<FileEntry>();
    }

    /// <summary>获取多个文件/目录的元信息。POST multipart(param=PathsListJSON)</summary>
    public async Task<List<FileEntry>> GetMetaAsync(IEnumerable<string> paths, CancellationToken ct = default)
    {
        var envelope = new PathListEnvelope
        {
            List = paths.Select(p => new PathItem { Path = p }).ToList(),
        };

        var json = JsonSerializer.Serialize(envelope);
        var url = GeneratePcsUrl("file", "meta");

        var result = await SendJsonAsync<FileListEnvelope>(
            ApiRequestType.Pcs,
            OperationFilesDirectoriesMeta,
            HttpMethod.Post,
            url,
            content: CreateParamMultipart(json),
            ct: ct).ConfigureAwait(false);

        return result.List ?? new List<FileEntry>();
    }
    /// <summary>按文件名搜索。GET /rest/2.0/pcs/file?method=search</summary>
    public async Task<List<FileEntry>> SearchAsync(
        string targetPath,
        string keyword,
        bool recursive,
        CancellationToken ct = default)
    {
        var url = GeneratePcsUrl("file", "search", new Dictionary<string, string>
        {
            ["path"] = targetPath,
            ["wd"] = keyword,
            ["re"] = recursive ? "1" : "0",
        });

        var envelope = await SendJsonAsync<FileListEnvelope>(
            ApiRequestType.Pcs,
            OperationSearch,
            HttpMethod.Get,
            url,
            ct: ct).ConfigureAwait(false);

        return envelope.List ?? new List<FileEntry>();
    }

    /// <summary>创建目录。POST /rest/2.0/pcs/file?method=mkdir</summary>
    public Task MkdirAsync(string path, CancellationToken ct = default)
    {
        var url = GeneratePcsUrl("file", "mkdir", new Dictionary<string, string> { ["path"] = path });
        return SendAndCheckAsync(ApiRequestType.Pcs, OperationMkdir, HttpMethod.Post, url, ct: ct);
    }

    /// <summary>批量删除文件/目录。POST multipart(param=PathsListJSON)</summary>
    public Task RemoveAsync(IEnumerable<string> paths, CancellationToken ct = default)
    {
        var envelope = new PathListEnvelope
        {
            List = paths.Select(p => new PathItem { Path = p }).ToList(),
        };

        var json = JsonSerializer.Serialize(envelope);
        var url = GeneratePcsUrl("file", "delete");
        return SendAndCheckAsync(
            ApiRequestType.Pcs,
            OperationRemove,
            HttpMethod.Post,
            url,
            content: CreateParamMultipart(json),
            ct: ct);
    }

    /// <summary>重命名（移动）单个文件/目录。</summary>
    public Task RenameAsync(string from, string to, CancellationToken ct = default)
        => MoveCoreAsync(OperationRename, new[] { (From: from, To: to) }, ct);

    /// <summary>批量移动文件/目录。</summary>
    public Task MoveAsync(IEnumerable<(string From, string To)> operations, CancellationToken ct = default)
        => MoveCoreAsync(OperationMove, operations, ct);

    /// <summary>批量拷贝文件/目录。</summary>
    public Task CopyAsync(IEnumerable<(string From, string To)> operations, CancellationToken ct = default)
    {
        var envelope = BuildCopyMoveEnvelope(operations);
        var json = JsonSerializer.Serialize(envelope);
        var url = GeneratePcsUrl("file", "copy");
        return SendAndCheckAsync(
            ApiRequestType.Pcs,
            OperationCopy,
            HttpMethod.Post,
            url,
            content: CreateParamMultipart(json),
            ct: ct);
    }

    private static CopyMoveEnvelope BuildCopyMoveEnvelope(IEnumerable<(string From, string To)> operations)
        => new()
        {
            List = operations.Select(op => new CopyMoveItem { From = op.From, To = op.To }).ToList(),
        };

    private Task MoveCoreAsync(string operation, IEnumerable<(string From, string To)> operations, CancellationToken ct)
    {
        var envelope = BuildCopyMoveEnvelope(operations);
        var json = JsonSerializer.Serialize(envelope);
        var url = GeneratePcsUrl("file", "move");
        return SendAndCheckAsync(
            ApiRequestType.Pcs,
            operation,
            HttpMethod.Post,
            url,
            content: CreateParamMultipart(json),
            ct: ct);
    }
}
