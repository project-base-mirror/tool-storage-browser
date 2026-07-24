using S3Explorer.Core;

namespace S3Explorer.Infrastructure.S3;

internal sealed class ThrottledReadStream : Stream
{
    private readonly Stream _inner;
    private readonly ITransferBandwidthLimiter _limiter;
    private readonly TransferDirection _direction;
    private readonly Action<int>? _onRead;
    private readonly bool _leaveOpen;

    public ThrottledReadStream(
        Stream inner,
        ITransferBandwidthLimiter limiter,
        TransferDirection direction,
        Action<int>? onRead = null,
        bool leaveOpen = false)
    {
        _inner = inner;
        _limiter = limiter;
        _direction = direction;
        _onRead = onRead;
        _leaveOpen = leaveOpen;
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => _inner.Length;
    public override long Position { get => _inner.Position; set => _inner.Position = value; }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = _inner.Read(buffer, offset, count);
        if (read > 0)
        {
            _limiter.WaitAsync(_direction, read, CancellationToken.None)
                .AsTask()
                .GetAwaiter()
                .GetResult();
            _onRead?.Invoke(read);
        }
        return read;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        var read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (read > 0)
        {
            await _limiter.WaitAsync(_direction, read, cancellationToken).ConfigureAwait(false);
            _onRead?.Invoke(read);
        }
        return read;
    }

    public override async Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        var read = await _inner.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
        if (read > 0)
        {
            await _limiter.WaitAsync(_direction, read, cancellationToken).ConfigureAwait(false);
            _onRead?.Invoke(read);
        }
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Flush() => _inner.Flush();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_leaveOpen)
            _inner.Dispose();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (!_leaveOpen)
            await _inner.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}
