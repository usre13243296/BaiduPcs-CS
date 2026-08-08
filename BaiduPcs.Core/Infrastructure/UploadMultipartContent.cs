using System.Net;
using System.Text;

namespace BaiduPcs.Core.Infrastructure;

/// <summary>
/// 分片上传的 multipart 内容：单文件字段 uploadedfile、filename 空串，
/// 精确对齐 Go 端 multipartreader.AddFormFile("uploadedfile", "", r)：
/// --{boundary}\r\nContent-Disposition: form-data; name="uploadedfile"; filename=""\r\n\r\n{data}\r\n--{boundary}--\r\n
/// 流式序列化，不整块载入内存（上传分片 4MB 起）。
/// 注意：BCL 的 MultipartFormDataContent 序列化时会省略空 FileName，故手写。
/// </summary>
internal sealed class UploadMultipartContent : HttpContent
{
    private readonly Stream _content;
    private readonly byte[] _header;
    private readonly byte[] _footer;

    public UploadMultipartContent(Stream content)
    {
        _content = content;
        var boundary = "---------------------------" + Guid.NewGuid().ToString("N");
        _header = Encoding.ASCII.GetBytes(
            $"--{boundary}\r\nContent-Disposition: form-data; name=\"uploadedfile\"; filename=\"\"\r\n\r\n");
        _footer = Encoding.ASCII.GetBytes($"\r\n--{boundary}--\r\n");
        Headers.TryAddWithoutValidation("Content-Type", $"multipart/form-data; boundary={boundary}");
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        => SerializeCoreAsync(stream, CancellationToken.None);

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken ct)
        => await SerializeCoreAsync(stream, ct).ConfigureAwait(false);

    private async Task SerializeCoreAsync(Stream stream, CancellationToken ct)
    {
        await stream.WriteAsync(_header, ct).ConfigureAwait(false);
        await _content.CopyToAsync(stream, ct).ConfigureAwait(false);
        await stream.WriteAsync(_footer, ct).ConfigureAwait(false);
    }

    protected override bool TryComputeLength(out long length)
    {
        if (_content.CanSeek)
        {
            length = _header.Length + _content.Length + _footer.Length;
            return true;
        }

        length = 0;
        return false;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _content.Dispose();
        }

        base.Dispose(disposing);
    }
}
