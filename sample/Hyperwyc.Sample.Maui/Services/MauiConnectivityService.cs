using Hyperwyc.Interfaces;

namespace Hyperwyc.Sample.Maui.Services;


public sealed class MauiConnectivityService : IConnectivityService, IDisposable
{
    private readonly object _gate = new();
    private readonly List<IObserver<bool>> _observers = [];

    private bool _lastPublished;

    private bool _disposed;

    public bool IsConnected => Connectivity.Current.NetworkAccess == NetworkAccess.Internet;

    public IObservable<bool> ConnectivityChanged { get; }

    public MauiConnectivityService()
    {
        _lastPublished = IsConnected;
        ConnectivityChanged = new ChangeStream(this);

        Connectivity.Current.ConnectivityChanged += OnPlatformConnectivityChanged;
    }

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
            if (_disposed || connected == _lastPublished) return;

            _lastPublished = connected;
            observers = [.. _observers];
        }

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
