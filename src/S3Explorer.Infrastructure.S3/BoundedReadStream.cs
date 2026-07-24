namespace S3Explorer.Infrastructure.S3;

internal sealed class BoundedReadStream : Stream
{
    private readonly Stream _inner;
    private readonly long _start;
    private readonly long _length;
    private readonly bool _leaveOpen;
    private long _position;

    public BoundedReadStream(Stream inner, long length, bool leaveOpen = false)
    {
        if (!inner.CanRead || !inner.CanSeek)
            throw new ArgumentException("分片源流必须支持读取和定位。", nameof(inner));
        if (length < 0)
            throw new ArgumentOutOfRangeException(nameof(length));
        _inner = inner;
        _start = inner.Position;
        _length = length;
        _leaveOpen = leaveOpen;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _length;
    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var allowed = AllowedCount(count);
        if (allowed == 0) return 0;
        var read = _inner.Read(buffer, offset, allowed);
        _position += read;
        return read;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        var allowed = AllowedCount(buffer.Length);
        if (allowed == 0) return 0;
        var read = await _inner.ReadAsync(buffer[..allowed], cancellationToken).ConfigureAwait(false);
        _position += read;
        return read;
    }

    public override async Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        var allowed = AllowedCount(count);
        if (allowed == 0) return 0;
        var read = await _inner.ReadAsync(buffer, offset, allowed, cancellationToken).ConfigureAwait(false);
        _position += read;
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        var target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        if (target < 0 || target > _length)
            throw new IOException("分片流定位超出范围。");
        _inner.Position = _start + target;
        _position = target;
        return _position;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_leaveOpen) _inner.Dispose();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (!_leaveOpen) await _inner.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private int AllowedCount(int requested) =>
        (int)Math.Min(requested, Math.Max(0, _length - _position));
}
