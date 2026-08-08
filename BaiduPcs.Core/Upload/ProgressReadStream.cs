namespace BaiduPcs.Core.Upload;

/// <summary>
/// 上传分片限长 + 进度插桩流：包装底层读流，只暴露 [start, start+length) 范围内的字节
/// （multipart 流式序列化只会读到分片边界，不会把文件剩余部分误发；对齐 Go 的 ReaderLen64 限长语义）。
/// 可选回调在读取时报告已读字节（读多少 ≈ 发多少，对齐下载侧"边读边报"）；
/// 分片失败重试时传 null 回调静默重传，避免重复计字节导致进度虚高。
/// </summary>
internal sealed class ProgressReadStream : Stream
{
    private readonly Stream _inner;
    private readonly Action<long>? _onBytesRead;
    private long _remaining;
    private long _position;

    public ProgressReadStream(Stream inner, long start, long length, Action<long>? onBytesRead)
    {
        _inner = inner;
        _remaining = length;
        _position = 0;
        _onBytesRead = onBytesRead;
        inner.Position = start;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_remaining <= 0)
        {
            return 0;
        }

        var read = _inner.Read(buffer, offset, (int)Math.Min(count, _remaining));
        Advance(read);
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_remaining <= 0)
        {
            return 0;
        }

        var read = await _inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, _remaining)], cancellationToken).ConfigureAwait(false);
        Advance(read);
        return read;
    }

    private void Advance(int read)
    {
        if (read <= 0)
        {
            return;
        }

        _remaining -= read;
        _position += read;
        _onBytesRead?.Invoke(read);
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;

    /// <summary>分片长度（multipart 用它计算 Content-Length）。</summary>
    public override long Length => _position + _remaining;

    /// <summary>已读字节数（只读；multipart 序列化不 Seek）。</summary>
    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
