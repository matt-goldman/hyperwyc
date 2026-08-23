using Hyperwyc.Interfaces;

namespace Hyperwyc.Sample.Maui.Services;

/// <summary>
/// Reports connectivity to Hyperwyc using MAUI Essentials. Copy this into your own app —
/// it is reference code, not a component to reference.
/// </summary>
/// <remarks>
/// <para>
/// Hyperwyc does not ship this. Reaching <c>Connectivity.Current</c> needs MAUI Essentials,
/// which would put MAUI into the dependency graph of every console, service and Blazor
/// consumer of a library whose whole premise is platform independence. Two members is not
/// enough to justify that.
/// </para>
/// <para>
/// The choices below are deliberate; the README explains each at greater length.
/// </para>
/// </remarks>
public sealed class MauiConnectivityService : IConnectivityService, IDisposable
{
    private readonly object _gate = new();
    private readonly List<IObserver<bool>> _observers = [];

    private bool _lastPublished;

    private bool _disposed;

    /// <summary>Whether the device can currently reach the internet.</summary>
    /// <remarks>
    /// <see cref="NetworkAccess.ConstrainedInternet"/> counts as disconnected — the
    /// conservative reading, on the grounds that a captive portal is not the internet. Flip
    /// this if your API is reachable under it.
    /// </remarks>
    public bool IsConnected => Connectivity.Current.NetworkAccess == NetworkAccess.Internet;

    /// <summary>A stream of connectivity <em>changes</em>.</summary>
    /// <remarks>
    /// Nothing is replayed on subscribe: this reports transitions, and
    /// <see cref="IsConnected"/> answers "right now". An earlier version seeded a subject with
    /// <see langword="false"/> at startup, which told every subscriber "offline" on connect
    /// while <see cref="IsConnected"/> read live state and disagreed.
    /// </remarks>
    public IObservable<bool> ConnectivityChanged { get; }

    // Hand-rolled rather than a BehaviorSubject: a System.Reactive reference for one field is
    // a dependency this app does not otherwise need, and Hyperwyc implements IObservable<T>
    // the same way internally. Use a subject instead if you already have Rx.
    public MauiConnectivityService()
    {
        _lastPublished = IsConnected;
        ConnectivityChanged = new ChangeStream(this);

        Connectivity.Current.ConnectivityChanged += OnPlatformConnectivityChanged;
    }

    /// <remarks>
    /// <c>Connectivity.Current</c> is a long-lived static, so a handler left attached keeps
    /// this instance — and everything it captures — alive for the process lifetime. Moot for
    /// an app-lifetime singleton, but reference code gets copied into places where it is not
    /// one. Note <see cref="IConnectivityService"/> is not <see cref="IDisposable"/>: Hyperwyc
    /// never disposes your instance, but a DI container disposes a singleton it constructed.
    /// </remarks>
    public void Dispose()
    {
        IObserver<bool>[] observers;

        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;

            observers = [.. _observers];
            _observers.Clear();
        }

        Connectivity.Current.ConnectivityChanged -= OnPlatformConnectivityChanged;

        foreach (var observer in observers)
            observer.OnCompleted();
    }

    private void OnPlatformConnectivityChanged(object? sender, ConnectivityChangedEventArgs e)
    {
        var connected = e.NetworkAccess == NetworkAccess.Internet;

        IObserver<bool>[] observers;

        lock (_gate)
        {
            // MAUI raises this for any change in network access, including Wi-Fi to cellular
            // while staying online. Forwarding that as a restoration triggers a flush with
            // nothing to send, so only an actual change is published.
            if (_disposed || connected == _lastPublished) return;

            _lastPublished = connected;
            observers = [.. _observers];
        }

        // Outside the lock, and on whatever thread the platform used — usually not the UI
        // thread. Marshalling here would force a dispatcher dependency into the service and
        // make it untestable; subscribers touching UI marshal for themselves.
        foreach (var observer in observers)
            observer.OnNext(connected);
    }

    private void Subscribe(IObserver<bool> observer)
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                _observers.Add(observer);
                return;
            }
        }

        observer.OnCompleted();
    }

    private void Unsubscribe(IObserver<bool> observer)
    {
        lock (_gate)
            _observers.Remove(observer);
    }

    private sealed class ChangeStream(MauiConnectivityService owner) : IObservable<bool>
    {
        public IDisposable Subscribe(IObserver<bool> observer)
        {
            ArgumentNullException.ThrowIfNull(observer);

            owner.Subscribe(observer);
            return new Subscription(owner, observer);
        }

        private sealed class Subscription(MauiConnectivityService owner, IObserver<bool> observer)
            : IDisposable
        {
            private IObserver<bool>? _observer = observer;

            public void Dispose()
            {
                var subscribed = Interlocked.Exchange(ref _observer, null);
                if (subscribed is not null)
                    owner.Unsubscribe(subscribed);
            }
        }
    }
}
