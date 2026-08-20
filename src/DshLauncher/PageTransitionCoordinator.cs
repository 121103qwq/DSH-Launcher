namespace DshLauncher;

/// <summary>
/// A single last-request-wins transition lane. Beginning a request invalidates
/// every older generation and cancels its token; cancellation also invalidates
/// the current generation so no late animation callback can publish state.
/// </summary>
internal sealed class PageTransitionCoordinator : IDisposable
{
    private readonly object _gate = new();
    private CancellationTokenSource? _activeCancellation;
    private long _generation;
    private bool _disposed;

    internal long CurrentGeneration
    {
        get
        {
            lock (_gate)
            {
                return _generation;
            }
        }
    }

    internal CancellationToken CurrentToken
    {
        get
        {
            lock (_gate)
            {
                return _activeCancellation?.Token ?? CancellationToken.None;
            }
        }
    }

    internal PageTransitionRequest Begin(CancellationToken cancellationToken = default)
    {
        CancellationTokenSource? previous;
        PageTransitionRequest request;

        lock (_gate)
        {
            ThrowIfDisposed();

            previous = _activeCancellation;
            _generation = unchecked(_generation + 1);
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _activeCancellation = cancellation;
            request = new PageTransitionRequest(_generation, cancellation.Token);
        }

        CancelAndDispose(previous);
        return request;
    }

    internal bool IsCurrent(long generation)
    {
        lock (_gate)
        {
            return !_disposed && _generation == generation;
        }
    }

    internal bool IsCurrent(PageTransitionRequest request) =>
        IsCurrent(request.Generation) && !request.CancellationToken.IsCancellationRequested;

    internal long Cancel()
    {
        CancellationTokenSource? previous;
        lock (_gate)
        {
            previous = _activeCancellation;
            _activeCancellation = null;
            _generation = unchecked(_generation + 1);
        }

        CancelAndDispose(previous);
        return CurrentGeneration;
    }

    public void Dispose()
    {
        CancellationTokenSource? previous;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            previous = _activeCancellation;
            _activeCancellation = null;
            _generation = unchecked(_generation + 1);
        }

        CancelAndDispose(previous);
    }

    private static void CancelAndDispose(CancellationTokenSource? cancellation)
    {
        if (cancellation is null)
        {
            return;
        }

        try
        {
            cancellation.Cancel(throwOnFirstException: false);
        }
        catch (AggregateException)
        {
            // Cancellation is an invalidation signal. A consumer callback must
            // not prevent a newer navigation request or window cleanup.
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(PageTransitionCoordinator));
        }
    }
}

internal readonly record struct PageTransitionRequest(
    long Generation,
    CancellationToken CancellationToken)
{
    internal CancellationToken Token => CancellationToken;

    internal bool IsCancellationRequested => CancellationToken.IsCancellationRequested;
}
