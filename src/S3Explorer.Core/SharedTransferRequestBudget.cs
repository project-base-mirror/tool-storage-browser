namespace S3Explorer.Core;

/// <summary>Bounds active data requests across files and their parts, not OS threads.</summary>
public sealed class SharedTransferRequestBudget
{
    public static SharedTransferRequestBudget Default { get; } = new(16);
    private readonly SemaphoreSlim _slots;

    public SharedTransferRequestBudget(int maximumRequests)
    {
        if (maximumRequests is < 1 or > 1024)
            throw new ArgumentOutOfRangeException(nameof(maximumRequests));
        _slots = new SemaphoreSlim(maximumRequests, maximumRequests);
    }

    public async ValueTask<IDisposable> EnterAsync(CancellationToken cancellationToken)
    {
        await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Lease(_slots);
    }

    private sealed class Lease(SemaphoreSlim slots) : IDisposable
    {
        private SemaphoreSlim? _slots = slots;
        public void Dispose() => Interlocked.Exchange(ref _slots, null)?.Release();
    }
}
