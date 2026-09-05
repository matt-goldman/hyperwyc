# Connectivity

Hyperwyc needs to know whether the device can reach the network, and it has no default for this on purpose. This is the one thing you must supply.

Hyperwyc needs to know whether the device can reach the network, and it has **no default for
this, on purpose**.

That is a deliberate exception to how the rest of the library behaves. Hyperwyc can pick a
store for you because any durable store will do. It cannot pick a connectivity source, because
the right answer depends on the platform — and a wrong one fails quietly. If it assumed
"always online", every request would take the network path, nothing would ever be queued, and
nothing would ever be replayed. You'd have a caching library that looked like it was working.

## Register it in your container

```csharp
services.AddSingleton<IConnectivityService, MyConnectivityService>();
```

That's it. Order doesn't matter — before or after `AddHyperwyc()`, whichever suits how your
registrations are organised, and it works the same if something else in your startup registers
it on your behalf.

If you'd rather keep the configuration in one place, or you already hold an instance, set it on
the options instead:

```csharp
services.AddHyperwyc(options =>
{
    options.Connectivity = myConnectivityService;
});
```

A container registration wins if you do both. Do neither and Hyperwyc throws the first time
anything needs it, with a message listing these options.

## Which implementation

**Write one against your platform.** `IConnectivityService` is two members, so this is short —
and it's what most apps should use.

```csharp
public interface IConnectivityService
{
    bool IsConnected { get; }
    IObservable<bool> ConnectivityChanged { get; }
}
```

It's a copy rather than a package because taking it as a dependency would put MAUI in
Hyperwyc's dependency graph for everyone, including the console apps, services and Blazor hosts
that have no use for it. Owning it costs you very little, and it leaves you free to define
"connected" as your app needs — treating `ConstrainedInternet` as offline, say, or folding in a
health check against your own API.

The implementation below is the one in the [sample app](../sample/Hyperwyc.Sample.Maui/Services/MauiConnectivityService.cs),
proven on an Android device: with Wi-Fi and mobile data disabled it reports disconnected, which
is what routes a read to the cache.

<details>
<summary><b>MauiConnectivityService</b> — copy this</summary>

```csharp
using Hyperwyc.Interfaces;

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
```

</details>

Five things in there are deliberate, and worth understanding before you change them:

**No `System.Reactive`.** The first version used a `BehaviorSubject<bool>`, which meant a
package reference existing for one field. Hand-rolling matches how Hyperwyc implements
`IObservable<T>` internally and keeps a dependency out of your app that Hyperwyc deliberately
avoids. If you already use Rx, by all means use a subject — the interface doesn't care.

**A change stream, not a state view.** Nothing is replayed on subscribe; `IsConnected` answers
"right now". Getting this wrong is easy: an earlier version seeded a subject with `false` at
startup, so a subscriber was told "offline" on connect while `IsConnected` read live state and
said otherwise.

**Only publish on an actual change.** MAUI raises `ConnectivityChanged` for any change in
network access, including moving between Wi-Fi and cellular while staying online. Forwarding
that as a connectivity restoration triggers a flush with nothing to send, so the service
compares against the last value it published — seeded from live state — and stays quiet
otherwise.

**`IDisposable`, to unhook the platform event.** `Connectivity.Current` is a long-lived static,
so a handler left attached keeps the service and everything it captures alive for the process
lifetime. Irrelevant for an app-lifetime singleton, but reference code gets copied into places
where it isn't one. Note that `IConnectivityService` itself is not `IDisposable` — Hyperwyc
never disposes your instance. Register it as a singleton and the container will.

**Events arrive on whatever thread the platform raised them on**, which on MAUI is usually not
the UI thread. That's intentional: forcing a dispatcher dependency into the service would make
it untestable and useless off-platform. Marshal in your subscriber if you're touching UI.

`NetworkAccess.ConstrainedInternet` counts as disconnected here — the conservative reading, on
the grounds that a captive portal is not the internet. If your API is reachable under it, flip
that condition.

**`NetworkAvailabilityConnectivityService`** ships in the box if you'd rather not, built on
`NetworkInterface.GetIsNetworkAvailable()` with no platform dependency:

```csharp
services.AddSingleton<IConnectivityService, NetworkAvailabilityConnectivityService>();
```

> **It reports whether a network is available, not whether your API is reachable.** It catches
> the hard-offline cases — aeroplane mode, Wi-Fi off, cable unplugged — but reports connected
> behind a captive portal, on a router with no upstream, or on a mobile signal too weak to
> carry a request.

In practice that costs less than it sounds like. Connectivity is a hint about which path to
take; a false positive means the request goes out and fails at the transport, which Hyperwyc
already handles by abandoning the flush and waiting for the next signal. You lose a wasted
attempt and some latency, not correctness. It's a reasonable choice for a desktop or server
host, and a reasonable starting point on mobile until you write the platform version.

**`AlwaysOnlineConnectivityService`** reports connected, always. Legitimate for a host that
genuinely is — or when you want the response cache and nothing else. **Nothing is ever queued
or replayed under it**, so don't reach for it just to get past the startup error.

## Faking connectivity in your own tests

Hyperwyc ships no test double, deliberately: `IConnectivityService` is two members, so a mocking
library does it in a line and a hand-written fake does it in a few. Shipping one in the main
package would mean a type you can accidentally reference from production code, and a separate
testing package is not worth publishing for this.

Most tests never need the change stream, because they drive sync explicitly with
`IHyperwyc.FlushAsync()` rather than waiting for a connectivity event. That makes the fake
almost nothing:

```csharp
internal sealed class FakeConnectivity(bool connected = true) : IConnectivityService
{
    public bool IsConnected { get; set; } = connected;

    public IObservable<bool> ConnectivityChanged { get; } = new Never();

    private sealed class Never : IObservable<bool>
    {
        public IDisposable Subscribe(IObserver<bool> observer) => new Noop();
        private sealed class Noop : IDisposable { public void Dispose() { } }
    }
}
```

```csharp
var connectivity = new FakeConnectivity(connected: false);

services.AddSingleton<IConnectivityService>(connectivity);
// ... queue a write while offline ...

connectivity.IsConnected = true;
await hyperwyc.FlushAsync();
```

If you're testing the automatic flush on connectivity restoration rather than an explicit one,
you need the stream to emit — reach for your mocking library's observable support, or a
`Subject<bool>` from `System.Reactive` in the test project only. And if a test is simply online
throughout, `AlwaysOnlineConnectivityService` is already the fake you want.
