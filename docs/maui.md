# Hyperwyc in a .NET MAUI app

This page gives you everything a .NET MAUI app needs. If you follow it top to bottom you will have offline reads, durable offline writes, a proper connectivity signal, a key that survives a restore, and a store the OS will not replay out of a backup.

**You do not have to read [Connectivity](connectivity.md).** Copy the class below and move on. That page is there for when the copy-paste is not right for you.

## 1. Install and register

```bash
dotnet add package Hyperwyc
```

In `MauiProgram.cs`:

```csharp
builder.Services
    .AddHttpClient("MyApi", c => c.BaseAddress = new Uri("https://api.example.com"))
    .AddHyperwycHandler()
    .AddHttpMessageHandler<AuthHandler>();   // yours, if you have one

builder.Services.AddHyperwyc();
builder.Services.AddSingleton<IConnectivityService, MauiConnectivityService>();
```

**Register `AddHyperwycHandler()` first**, before your auth handler. Handlers added after it run on replays too, so a write queued on Monday is authenticated with Tuesday's token. See [Pipeline placement](pipeline.md).

`AddHyperwycHandler()` also works on a typed client — `AddHttpClient<IProductsApi, ProductsApi>()` — which is the registration most MAUI apps actually use.

## 2. The connectivity service

The `MauiConnectivityService` is provided here as a sample to copy, rather than being included in the package, to prevent Hyperwyc included .NET MAUI dependencies for consumers that don't need it. Copy it as-is; it is proven on an Android device, and with Wi-Fi and mobile data disabled it reports disconnected.

<details>
<summary><b><code>MauiConnectivityService</code></b> — copy this into your app</summary>

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

Five things worth calling out in case you want or need to change them:

1. **No `System.Reactive`.** The first version used a `BehaviorSubject<bool>`, which meant a package reference existing for one field, but Hyperwyc deliberately avoids a dependency on `System.Reactive`, so this version matches how Hyperwyc implements `IObservable<T>` internally. However, the `BehaviorSubject` version is better, and if you already use Rx, then you can use it here instead; and if you don't it's likely worth considering in your mobile app anyway for several reasons.
2. **A change stream, not a state view.** Nothing is replayed on subscribe; `IsConnected` provides the point-in-ime state. Getting this wrong is easy: an earlier version seeded a subject with `false` at startup, so a subscriber was told "offline" on connect while `IsConnected` read live state and said otherwise.
3. **Only publish on an actual change.** .NET MAUI raises `ConnectivityChanged` for any change in network access, including moving between Wi-Fi and cellular while staying online. Forwarding that as a connectivity restoration triggers a flush with nothing to send, so the service compares against the last value it published, seeded from live state, and only sends a new value if there's a meaningful change.
4. **`IDisposable`, to unhook the platform event.** `Connectivity.Current` is a long-lived static, so a handler left attached keeps the service and everything it captures alive for the process lifetime. Irrelevant for an app-lifetime singleton, but reference code often gets copied verbatim into other scenarios. Note that `IConnectivityService` itself is not `IDisposable`; Hyperwyc never disposes your instance. Register it as a singleton and let the container handle it.
5. **Events arrive on whatever thread the platform raised them on**, which on .NET MAUI is usually _not_ the UI thread. That's usually the correct approach, as you may not always want or need the UI to respond to the events. Also, forcing a dispatcher dependency into the service would make it untestable and useless off-platform. Respond to events on the UI thread as required (e.g. if you need to notify a user of a post-reconnection send success or failure).

`NetworkAccess.ConstrainedInternet` counts as disconnected here, on the grounds that a captive portal is not the internet. If your API is reachable under one, flip that condition.

## 3. Custom storage encryption key

By default the store is encrypted with a key derived from its own path. That requires no setup, and protects cached data from casual inspection, but the key is deterministic, so it is not a defense against someone who has the device and knows what this library does.

If the cached data warrants more, hold the key in `SecureStorage`:

```csharp
// Retrieve or create a custom unique key:

var key = await SecureStorage.GetAsync("hyperwyc-key");

if (key is null)
{
    key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    await SecureStorage.SetAsync("hyperwyc-key", key);
}

// use it in Hyperwyc options

builder.Services.AddHyperwyc(configureStore: store =>
{
    store.EncryptionKey = Convert.FromBase64String(key);   // 32 bytes
});
```

**Note that losing that key means losing everything already stored.** This includes queued writes. This is a decision you will need to balance: the derived key is weaker, and it is also the reason a restore onto a new device cannot read the old outbox, which is a hazard the next section is about.

## 4. Exclude the store from OS backup

**Do this.** It is two lines, and the reason is important:

> The outbox is a list of writes that have not happened yet. Restore a three-week-old backup and
> Hyperwyc will faithfully replay a sale that was delivered a fortnight ago. Restore onto a second
> device and both hold the same pending writes, and both will send them.

Hyperwyc has no duplicate suppression, by design, so nothing catches it.

- **iOS** — set `NSURLIsExcludedFromBackupKey` on the store directory once at startup.
- **Android** — an `<exclude domain="file" path="…"/>` entry in `data_extraction_rules` (API 31+) or `full_backup_content` below that. Use the `<cloud-backup>` / `<device-transfer>` split: a direct device-to-device transfer does not carry the duplicate-replay risk a cloud restore does.

`CabinetStoreOptions.DefaultDirectoryPath()` gives you the path.

[comment: the path is not helpful here. The code shown gets it at runtime but the exclusions go in the plist and manifest files.]

A secondary reason on Android: Auto Backup caps an app at 25 MB, and exceeding it silently stops backup for the whole app rather than just the offending files. Hyperwyc's cache is not currently bounded, so that ceiling is reachable — see [Storage](storage.md).

## 5. Know about AOT

iOS release builds have AOT on by default, and Hyperwyc's store currently serialises through reflection — there is no `JsonSerializerContext`. Test a release build on a device early rather than discovering it at submission. This is tracked and is not something you can work around from outside the library.

## Then what

Your existing calls are untouched:

```csharp
// Online: fetched and cached. Offline: served from cache.
var products = await client.GetFromJsonAsync<List<Product>>("/products");

// Online: sent. Offline: queued, and you get 202 Accepted.
var response = await client.PostAsJsonAsync("/sales", sale);
```

The one thing worth adding is a way to hear what happened to a deferred write — [Events](events.md) — and a "sync now" control if you show queued work to your users:

```csharp
await hyperwyc.FlushAsync();
```

If you want the guided version of all of this, the [tutorial](tutorial/) builds it up in five short pages on a desktop, where you can stop the API rather than needing a device.
