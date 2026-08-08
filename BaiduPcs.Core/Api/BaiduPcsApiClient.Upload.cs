using System.Text.Json;
using System.Text.Json.Serialization;
using BaiduPcs.Core.Infrastructure;
using BaiduPcs.Core.Util;

namespace BaiduPcs.Core.Api;

public sealed partial class BaiduPcsApiClient
{
    // ======================== 上传常量（对齐 Go baidupcs/upload.go） ========================

    /// <summary>上传分片最小大小（文件 &lt; 8GB）。</summary>
    public const long MinUploadBlockSize = 4 * 1024 * 1024;

    /// <summary>上传分片中等大小（8GB ≤ 文件 &lt; 32GB）。</summary>
    public const long MiddleUploadBlockSize = 16 * 1024 * 1024;

    /// <summary>上传分片最大大小（文件 ≥ 32GB）。</summary>
    public const long MaxUploadBlockSize = 64 * 1024 * 1024;

    /// <summary>中等分片对应的文件大小阈值。</summary>
    public const long MiddleUploadThreshold = 8L * 1024 * 1024 * 1024;

    /// <summary>最大分片对应的文件大小阈值。</summary>
    public const long MaxUploadThreshold = 32L * 1024 * 1024 * 1024;

    /// <summary>目前支持的最大文件大小。</summary>
    public const long MaxUploadSize = 128L * 1024 * 1024 * 1024;

    /// <summary>计算 slice-md5 所需的前缀长度（256KB）。</summary>
    public const long SliceMd5Size = 256 * 1024;

    /// <summary>秒传 data_content 子片段长度（4KB）。</summary>
    public const long ContentSize = 4 * 1024;

    /// <summary>秒传防滥用随机片段：data_offset 取模范围保护（fileSize-subSize+1 ≤ 1 时取 0）。</summary>
    public const long DataOffsetModuloMin = 1;

    // ======================== DTO ========================

    /// <summary>precreate（秒传预检 + 会话初始化）返回结果。</summary>
    public sealed class PrecreateResult
    {
        /// <summary>秒传是否命中（return_type == 2）。</summary>
        public bool IsRapidUpload { get; init; }

        /// <summary>秒传未命中时返回的分片上传会话 ID。</summary>
        public string? UploadId { get; init; }
    }

    private sealed class PrecreateEnvelope
    {
        [JsonPropertyName("return_type")]
        public int ReturnType { get; set; }

        [JsonPropertyName("uploadid")]
        public string? UploadId { get; set; }
    }

    private sealed class UploadTmpFileEnvelope
    {
        [JsonPropertyName("md5")]
        public string? Md5 { get; set; }
    }

    private sealed class PcsServerItem
    {
        [JsonPropertyName("server")]
        public string? Server { get; set; }
    }

    private sealed class PcsServerEnvelope
    {
        [JsonPropertyName("host")]
        public string? Host { get; set; }

        [JsonPropertyName("server")]
        public List<string>? Server { get; set; }

        [JsonPropertyName("servers")]
        public List<PcsServerItem>? Servers { get; set; }
    }

    // ======================== 秒传预检 + 会话初始化（二合一） ========================

    /// <summary>
    /// precreate：一次请求完成「秒传预检」与「分片上传会话初始化」。
    /// 服务器命中秒传（return_type=2）则直接完成；否则返回 uploadid 供后续 superfile2 分片上传。
    /// 对应 Go 端 rapidUploadV2（PrepareRapidUploadV2 参数，不含 bdstoken/sign）。
    /// </summary>
    /// <param name="targetPath">完整目标路径 /x/y/name.ext</param>
    /// <param name="size">文件总字节数</param>
    /// <param name="contentMd5">全文件 MD5（小写）</param>
    /// <param name="sliceMd5">文件前 256KB 的 MD5（小写）</param>
    /// <param name="blockList">按分片顺序的逐块 MD5（小写）</param>
    /// <param name="dataOffset">随机片段偏移（creaetDataOffset 计算结果）</param>
    /// <param name="dataContent">偏移处 4KB 内容 base64（去尾部 =）</param>
    /// <param name="localMtime">文件 mtime（unix 秒）</param>
    /// <param name="rtype">2=skip，3=overwrite</param>
    /// <param name="uploadId">续传时复用已有会话，首次传 null</param>
    public async Task<PrecreateResult> PrecreateAsync(
        string targetPath,
        long size,
        string contentMd5,
        string sliceMd5,
        IReadOnlyList<string> blockList,
        long dataOffset,
        string dataContent,
        long localMtime,
        int rtype,
        string? uploadId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(targetPath))
        {
            throw new System.ArgumentException("targetPath 不能为空", nameof(targetPath));
        }

        var fields = new List<KeyValuePair<string, string>>
        {
            new("path", targetPath),
            new("target_path", PcsPath.GetParent(targetPath) + "/"),
            new("size", size.ToString()),
            new("data_offset", dataOffset.ToString()),
            new("isdir", "0"),
            new("local_mtime", localMtime.ToString()),
            new("local_ctime", localMtime.ToString()),
            new("rtype", rtype.ToString()),
            new("checkexist", "0"),
            new("autoinit", "1"),
            new("content-md5", contentMd5.ToLowerInvariant()),
            new("slice-md5", sliceMd5.ToLowerInvariant()),
            new("data_time", localMtime.ToString()),
            new("data_length", ContentSize.ToString()),
            new("data_content", dataContent),
            new("block_list", JsonSerializer.Serialize(blockList)),
            new("mode", "1"),
        };
        if (!string.IsNullOrEmpty(uploadId))
        {
            fields.Add(new("uploadid", uploadId));
        }

        var url = GeneratePanUrl("precreate");
        var envelope = await SendJsonAsync<PrecreateEnvelope>(
            ApiRequestType.Pan,
            OperationPrecreate,
            HttpMethod.Post,
            url,
            content: CreateFormUrlEncoded(fields),
            ct: ct).ConfigureAwait(false);

        return new PrecreateResult
        {
            IsRapidUpload = envelope.ReturnType == 2,
            UploadId = string.IsNullOrEmpty(envelope.UploadId) ? null : envelope.UploadId,
        };
    }

    // ======================== 分片上传（superfile2） ========================

    /// <summary>
    /// 上传单个分片：POST multipart/form-data（文件字段名 uploadedfile，filename 空串）到
    /// https://{pcsHost}/rest/2.0/pcs/superfile2/upload?method=upload&amp;type=tmpfile&amp;...
    /// 对应 Go 端 UploadTmpFile（PrepareUploadSuperfile2 参数）。
    /// </summary>
    /// <param name="pcsHost">动态上传 host（locateupload 下发，如 nj01pcs*.pcs.baidu.com）；空则用默认 PCS 地址</param>
    /// <param name="targetPath">目标完整路径（与 precreate 一致）</param>
    /// <param name="uploadId">precreate 返回的会话 ID</param>
    /// <param name="partSeq">分片序号（从 0 开始）</param>
    /// <param name="partOffset">该分片在文件中的起始字节偏移</param>
    /// <param name="content">该分片字节流（由调用方创建，此处负责释放）</param>
    /// <returns>服务器返回的分片 md5</returns>
    public async Task<string> UploadTmpFileAsync(
        string pcsHost,
        string targetPath,
        string uploadId,
        int partSeq,
        long partOffset,
        Stream content,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(uploadId))
        {
            throw new System.ArgumentException("uploadId 不能为空", nameof(uploadId));
        }

        var builder = new UriBuilder
        {
            Scheme = _options.Https ? "https" : "http",
            Host = string.IsNullOrEmpty(pcsHost)
                ? (string.IsNullOrEmpty(_options.PcsAddr) ? ApiConstants.PcsBaiduCom : _options.PcsAddr)
                : pcsHost,
            Path = "/rest/2.0/pcs/superfile2",
        };
        var query = System.Web.HttpUtility.ParseQueryString(string.Empty);
        query["app_id"] = _options.AppId.ToString();
        query["method"] = "upload";
        query["type"] = "tmpfile";
        query["path"] = targetPath;
        query["partseq"] = partSeq.ToString();
        query["partoffset"] = partOffset.ToString();
        query["uploadid"] = uploadId;
        query["vip"] = "1";
        builder.Query = query.ToString();

        // multipart：字段名 uploadedfile、filename 空串（对齐 Go multipartreader.AddFormFile("uploadedfile", "", r)）
        using var form = new UploadMultipartContent(content);

        var envelope = await SendJsonAsync<UploadTmpFileEnvelope>(
            ApiRequestType.Pcs,
            OperationUploadTmpFile,
            HttpMethod.Post,
            builder.Uri,
            content: form,
            ct: ct).ConfigureAwait(false);

        if (string.IsNullOrEmpty(envelope.Md5))
        {
            throw BaiduApiException.Other(OperationUploadTmpFile, "响应未返回 md5");
        }

        return envelope.Md5;
    }

    // ======================== 合并分片（create） ========================

    /// <summary>
    /// 全部（或续传后全部）分片上传成功后合并落盘。
    /// 对应 Go 端 UploadCreateSuperFile（PrepareUploadCreateSuperFile 参数）。
    /// </summary>
    /// <param name="targetPath">目标完整路径</param>
    /// <param name="size">文件总字节数</param>
    /// <param name="uploadId">会话 ID</param>
    /// <param name="blockList">逐块 md5，必须按 partseq 升序（对齐 Go sortBlockList）</param>
    /// <param name="rtype">2=skip，3=overwrite</param>
    public Task CreateAsync(
        string targetPath,
        long size,
        string uploadId,
        IReadOnlyList<string> blockList,
        int rtype,
        CancellationToken ct = default)
    {
        var fields = new List<KeyValuePair<string, string>>
        {
            new("uploadid", uploadId),
            new("path", targetPath),
            new("size", size.ToString()),
            new("isdir", "0"),
            new("rtype", rtype.ToString()),
            new("block_list", JsonSerializer.Serialize(blockList)),
            new("target_path", PcsPath.GetParent(targetPath)),
        };

        var url = GeneratePanUrl("create");
        return SendAndCheckAsync(
            ApiRequestType.Pan,
            OperationCreateSuperFile,
            HttpMethod.Post,
            url,
            content: CreateFormUrlEncoded(fields),
            ct: ct);
    }

    // ======================== 上传服务器（locateupload） ========================

    /// <summary>
    /// 获取推荐的上传 PCS 服务器列表（locateupload 下发，含 - 的 hostname 为动态节点，优先选用）。
    /// 对应 Go 端 GetRandomPCSHost（PreparePCSServers 参数）。
    /// 返回空列表时调用方应降级默认 pcs.baidu.com。
    /// </summary>
    public async Task<IReadOnlyList<string>> LocateUploadHostsAsync(CancellationToken ct = default)
    {
        var url = GeneratePcsUrl("file", "locateupload", new Dictionary<string, string>
        {
            ["upload_version"] = "2.0",
            ["app_id"] = ApiConstants.PanAppId,
        });

        var envelope = await SendJsonAsync<PcsServerEnvelope>(
            ApiRequestType.Pcs,
            OperationGetPcsServer,
            HttpMethod.Get,
            url,
            ct: ct).ConfigureAwait(false);

        var hosts = new List<string>();
        if (envelope.Servers is not null)
        {
            foreach (var item in envelope.Servers)
            {
                if (string.IsNullOrEmpty(item.Server) || !item.Server.Contains('-'))
                {
                    continue;
                }

                if (Uri.TryCreate(item.Server, UriKind.Absolute, out var uri))
                {
                    hosts.Add(uri.Host);
                }
                else
                {
                    hosts.Add(item.Server);
                }
            }
        }

        if (hosts.Count == 0 && envelope.Server is not null)
        {
            hosts.AddRange(envelope.Server.Where(s => !string.IsNullOrEmpty(s)));
        }

        return hosts;
    }
}
