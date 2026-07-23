namespace S3Explorer.Core;

public sealed class OperationCancellation : IDisposable
{
    private readonly object _gate = new();
    private CancellationTokenSource? _current;

    public CancellationToken StartNew()
    {
        CancellationTokenSource? previous;
        CancellationToken token;

        lock (_gate)
        {
            previous = _current;
            _current = new CancellationTokenSource();
            token = _current.Token;
        }

        CancelAndDispose(previous);
        return token;
    }

    public CancellationToken CurrentOrStart()
    {
        lock (_gate)
        {
            _current ??= new CancellationTokenSource();
            return _current.Token;
        }
    }

    public void CancelCurrent()
    {
        CancellationTokenSource? current;

        lock (_gate)
        {
            current = _current;
            _current = null;
        }

        CancelAndDispose(current);
    }

    public void Dispose() => CancelCurrent();

    private static void CancelAndDispose(CancellationTokenSource? source)
    {
        if (source is null)
            return;

        try
        {
            source.Cancel();
        }
        finally
        {
            source.Dispose();
        }
    }
}
