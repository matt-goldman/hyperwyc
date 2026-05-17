using hyperwyc.Models;

namespace hyperwyc;

/// <summary>
/// A minimal hand-rolled hot observable subject that broadcasts
/// <see cref="SyncEvent"/> values to all current subscribers.
/// No dependency on <c>System.Reactive</c> is required.
/// </summary>
/// <remarks>
/// <para>
/// Expose the instance as <see cref="IObservable{SyncEvent}"/> via
/// <c>Ihyperwyc.SyncEvents</c>. Only <c>hyperwycHandler</c> and the sync
/// orchestrator should call <see cref="Publish"/> — it is marked
/// <see langword="internal"/>.
/// </para>
/// <para>
/// Exceptions thrown inside a subscriber's <c>OnNext</c> are swallowed so
/// they cannot crash the pipeline or affect other subscribers.
/// </para>
/// </remarks>
public sealed class SyncEventStream : IObservable<SyncEvent>, IDisposable
{
    private readonly Lock _gate = new();
    private List<IObserver<SyncEvent>> _observers = [];
    private bool _disposed;

    // -------------------------------------------------------------------------
    // IObservable<SyncEvent>
    // -------------------------------------------------------------------------

    /// <inheritdoc/>
    public IDisposable Subscribe(IObserver<SyncEvent> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);

        lock (_gate)
        {
            if (_disposed)
            {
                observer.OnCompleted();
                return Disposable.Noop;
            }

            _observers = [.. _observers, observer];
        }

        return new Subscription(this, observer);
    }

    // -------------------------------------------------------------------------
    // Internal publishing API
    // -------------------------------------------------------------------------

    /// <summary>
    /// Broadcasts <paramref name="syncEvent"/> to all current subscribers.
    /// </summary>
    internal void Publish(SyncEvent syncEvent)
    {
        List<IObserver<SyncEvent>> snapshot;
        lock (_gate)
        {
            if (_disposed) return;
            snapshot = _observers;
        }

        foreach (var observer in snapshot)
        {
            try { observer.OnNext(syncEvent); }
            catch { /* individual subscriber errors must not affect others */ }
        }
    }

    /// <summary>
    /// Calls <c>OnError</c> on all current subscribers with the given exception,
    /// then clears the subscriber list.
    /// </summary>
    internal void PublishError(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        List<IObserver<SyncEvent>> snapshot;
        lock (_gate)
        {
            if (_disposed) return;
            snapshot = _observers;
            _observers = [];
        }

        foreach (var observer in snapshot)
        {
            try { observer.OnError(exception); }
            catch { /* ignore */ }
        }
    }

    // -------------------------------------------------------------------------
    // IDisposable
    // -------------------------------------------------------------------------

    /// <summary>
    /// Calls <c>OnCompleted</c> on all remaining subscribers and marks the
    /// stream as disposed. Subsequent subscriptions receive an immediate
    /// <c>OnCompleted</c>.
    /// </summary>
    public void Dispose()
    {
        List<IObserver<SyncEvent>> snapshot;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            snapshot = _observers;
            _observers = [];
        }

        foreach (var observer in snapshot)
        {
            try { observer.OnCompleted(); }
            catch { /* ignore */ }
        }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private void Unsubscribe(IObserver<SyncEvent> observer)
    {
        lock (_gate)
        {
            _observers = [.. _observers.Where(o => !ReferenceEquals(o, observer))];
        }
    }

    private sealed class Subscription(SyncEventStream stream, IObserver<SyncEvent> observer) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                stream.Unsubscribe(observer);
        }
    }

    private static class Disposable
    {
        internal static readonly IDisposable Noop = new NoopDisposable();
        private sealed class NoopDisposable : IDisposable { public void Dispose() { } }
    }
}
