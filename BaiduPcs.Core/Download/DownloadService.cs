using BaiduPcs.Core.Api;
using BaiduPcs.Core.Auth;
using BaiduPcs.Core.Infrastructure;

namespace BaiduPcs.Core.Download;

/// <summary>下载进度信息。</summary>
public sealed class DownloadProgress
{
    public long DownloadedBytes { get; init; }
    public long TotalBytes { get; init; }
    public double Percent => TotalBytes > 0 ? DownloadedBytes * 100.0 / TotalBytes : 0;
}

/// <summary>
/// 下载服务：获取直链后流式下载到本地，支持断点续传与进度回调。
/// </summary>
public sealed class DownloadService
{
    private readonly BaiduPcsApiClient _api;

    // 流式下载使用独立客户端：HttpClient.Timeout 覆盖整个请求（含流读取），
    // API 客户端的 50s 超时会把大文件下载强行取消，故此处不设总超时，
    // 由 CancellationToken 负责取消。
    private static readonly HttpClient DirectClient = new()
    {
        Timeout = System.Threading.Timeout.InfiniteTimeSpan,
    };

    public DownloadService(BaiduPcsApiClient api)
    {
        _api = api;
    }

    /// <summary>
    /// 下载远程文件到本地。若本地存在 "filename.part" 则自动从断点续传。
    /// 完成后将 .part 重命名为最终文件名。
    /// </summary>
    public async Task DownloadAsync(
        string remotePath,
        string localFilePath,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        // 使用 PCS 官方下载接口（method=download）：服务端按账号等级生成带签名直链并 302 重定向，
        // 客户端跟随重定向下载 —— 会员加速在此生效（locatedownload 手动直链被 CDN 限速）。
        var officialUrl = _api.GeneratePcsUrl("file", "download", new Dictionary<string, string>
        {
            ["path"] = remotePath,
        });

        var dir = Path.GetDirectoryName(localFilePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        // 断点续传：检查 .part 文件已下载大小
        var partPath = localFilePath + ".part";
        long existingSize = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;

        using var request = new HttpRequestMessage(HttpMethod.Get, officialUrl);
        if (existingSize > 0)
        {
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existingSize, null);
        }

        // 直链请求需携带 netdisk UA 与会话 cookie（部分直链需要）
        request.Headers.TryAddWithoutValidation("User-Agent", BaiduPcs.Core.Api.ApiConstants.DefaultUserAgent);
        request.Headers.TryAddWithoutValidation("Referer", "https://pan.baidu.com/");
        var cookieHeader = _api.Session.BuildCookieHeader();
        if (!string.IsNullOrEmpty(cookieHeader))
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        }

        // 直链流读取走无限超时的独立客户端，避免 API 客户端的 50s 总超时截断大文件
        using var response = await DirectClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var totalBytes = (response.Content.Headers.ContentLength ?? 0) + existingSize;

        await using var output = new FileStream(partPath, FileMode.Append, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

        var buffer = new byte[81920];
        long downloaded = existingSize;
        while (true)
        {
            var read = await input.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            downloaded += read;
            progress?.Report(new DownloadProgress
            {
                DownloadedBytes = downloaded,
                TotalBytes = totalBytes,
            });
        }

        await output.FlushAsync(ct).ConfigureAwait(false);

        // 下载完成：重命名 .part 为最终文件
        if (File.Exists(localFilePath))
        {
            File.Delete(localFilePath);
        }

        File.Move(partPath, localFilePath);
    }
}