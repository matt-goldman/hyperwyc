using System.Net.NetworkInformation;
using Hyperwyc.Interfaces;

namespace Hyperwyc;

/// <summary>
/// An <see cref="IConnectivityService"/> built on the BCL's
/// <see cref="NetworkInterface.GetIsNetworkAvailable"/>, with no platform dependency.
/// </summary>
/// <remarks>
/// <para>
/// <b>This reports whether a network is available, not whether your API is reachable.</b>
/// It answers "is there an operational, non-loopback interface", so it detects the common
/// hard-offline cases — aeroplane mode, Wi-Fi off, cable unplugged — but reports connected
/// for a captive portal, a router with no upstream, or a mobile signal too weak to carry a
/// request.
/// </para>
/// <para>
/// That is usually good enough: Hyperwyc treats connectivity as a hint about which path to
/// take, and a request that gets a false positive fails at the transport, which
/// <see cref="OutboxProcessor"/> already handles by abandoning the flush and waiting for
/// the next signal. What it costs is latency and a wasted attempt, not correctness.
/// </para>
/// <para>
/// <b>A VPN or mesh interface can hold this at <see langword="true"/> on its own.</b> The
/// underlying check excludes <see cref="NetworkInterfaceType.Tunnel"/>, but a TUN interface
/// frequently reports <see cref="NetworkInterfaceType.Unknown"/> instead and so is not
/// excluded — and it does not necessarily go down when the physical link does. Unlike the
/// cases above, that is not a degraded network path; it is not a path at all.
/// </para>
/// <para>
/// On .NET MAUI prefer a <c>Connectivity.Current</c>-based implementation, which
/// distinguishes <c>NetworkAccess.Internet</c> from <c>ConstrainedInternet</c> and reacts to
/// platform connectivity events properly. The sample application carries one to copy. On
/// Windows outside MAUI, <c>NetworkInformation.GetInternetConnectionProfile()</c> and
/// <c>GetNetworkConnectivityLevel()</c> report reachability rather than link state, at the
/// cost of a Windows-specific target framework.
/// </para>
/// </remarks>
public sealed class NetworkAvailabilityConnectivityService : IConnectivityService, IDisposable
{
    private readonly object _gate = new();
    private readonly List<IObserver<bool>> _observers = [];

    private bool _lastPublished;
    private bool _subscribedToPlatform;
    private bool _disposed;

    /// <inheritdoc/>
    public bool IsConnected => NetworkInterface.GetIsNetworkAvailable();

    /// <inheritdoc/>
    /// <remarks>
    /// A stream of changes, not a view of current state — nothing is replayed on subscribe.
    /// Ask <see cref="IsConnected"/> for the state right now.
    /// </remarks>
    public IObservable<bool> ConnectivityChanged { get; }

    /// <summary>
    /// Initialises the service and begins listening for network availability changes.
    /// </summary>
    public NetworkAvailabilityConnectivityService()
    {
        _lastPublished = SafeIsConnected();
        ConnectivityChanged = new ChangeStream(this);

        // Not every runtime supports change notifications — a browser host, for one. Losing
        // them degrades Hyperwyc to flushing on startup and on manual request, which is a
        // reduced service rather than a broken one, so it is not worth failing construction.
        try
        {
            NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
            _subscribedToPlatform = true;
        }
        catch (PlatformNotSupportedException)
        {
        }
        catch (NetworkInformationException)
        {
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        IObserver<bool>[] observers;
        bool unhook;

        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;

            unhook = _subscribedToPlatform;
            observers = [.. _observers];
            _observers.Clear();
        }

        if (unhook)
        {
            try
            {
                NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
            }
            catch (PlatformNotSupportedException)
            {
            }
            catch (NetworkInformationException)
            {
            }
        }

        foreach (var observer in observers)
            observer.OnCompleted();
    }

    private static bool SafeIsConnected()
    {
        try
        {
            return NetworkInterface.GetIsNetworkAvailable();
        }
        catch (NetworkInformationException)
        {
            // Unable to interrogate the interfaces. Assuming connected keeps requests
            // flowing to the transport, which reports the truth by succeeding or failing.
            return true;
        }
    }

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) =>
        PublishIfChanged(e.IsAvailable);

    /// <summary>
    /// Publishes <paramref name="isAvailable"/> to subscribers if it differs from the last
    /// value published.
    /// </summary>
    /// <remarks>
    /// Split out from the event handler because <see cref="NetworkAvailabilityEventArgs"/>
    /// cannot be constructed outside the BCL, so this is the only way to exercise the
    /// publishing rules in a test.
    /// </remarks>
    internal void PublishIfChanged(bool isAvailable)
    {
        IObserver<bool>[] observers;

        lock (_gate)
        {
            // Only an actual change is worth reporting: republishing "connected" triggers a
            // flush that has nothing new to act on.
            if (_disposed || isAvailable == _lastPublished) return;

            _lastPublished = isAvailable;
            observers = [.. _observers];
        }

        // Outside the lock: subscriber code is arbitrary and must not run while holding a
        // lock the platform's callback thread also needs.
        foreach (var observer in observers)
            observer.OnNext(isAvailable);
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

    private sealed class ChangeStream(NetworkAvailabilityConnectivityService owner) : IObservable<bool>
    {
        public IDisposable Subscribe(IObserver<bool> observer)
        {
            ArgumentNullException.ThrowIfNull(observer);

            owner.Subscribe(observer);
            return new Subscription(owner, observer);
        }

        private sealed class Subscription(
            NetworkAvailabilityConnectivityService owner,
            IObserver<bool> observer) : IDisposable
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
