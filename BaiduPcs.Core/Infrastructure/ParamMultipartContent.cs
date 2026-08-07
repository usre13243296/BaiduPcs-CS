using System.Net;
using System.Text;

namespace BaiduPcs.Core.Infrastructure;

/// <summary>
/// 精确对齐 Go 端 multipartreader 的 multipart 表单内容：
/// 单字段 "param"，Content-Disposition 带引号，且不带 multipart 子头。
/// 格式：--{boundary}\r\nContent-Disposition: form-data; name="param"\r\n\r\n{json}\r\n--{boundary}--\r\n
/// </summary>
internal sealed class ParamMultipartContent : HttpContent
{
    private readonly byte[] _body;

    public ParamMultipartContent(string json)
    {
        var boundary = "---------------------------" + Guid.NewGuid().ToString("N");
        var jsonBytes = Encoding.UTF8.GetBytes(json);

        using var stream = new MemoryStream();
        WriteAscii(stream, $"--{boundary}\r\nContent-Disposition: form-data; name=\"param\"\r\n\r\n");
        stream.Write(jsonBytes, 0, jsonBytes.Length);
        WriteAscii(stream, $"\r\n--{boundary}--\r\n");
        _body = stream.ToArray();

        Headers.TryAddWithoutValidation("Content-Type", $"multipart/form-data; boundary={boundary}");
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        => stream.WriteAsync(_body, CancellationToken.None).AsTask();

    protected override bool TryComputeLength(out long length)
    {
        length = _body.Length;
        return true;
    }

    private static void WriteAscii(Stream stream, string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        stream.Write(bytes, 0, bytes.Length);
    }
}
