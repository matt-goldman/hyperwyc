# Testing an app that uses Hyperwyc

How to exercise the offline paths in your own test suite.

## Faking connectivity

Hyperwyc ships no test double, deliberately: `IConnectivityService` is two members, so a mocking library does it in a line and a hand-written fake does it in a few. Shipping one in the main package would mean a type you can accidentally reference from production code, and a separate testing package is not worth publishing for this.

Most tests never need the change stream, because they drive delivery explicitly with `IHyperwyc.FlushAsync()` rather than waiting for a connectivity event. That makes the fake almost nothing:

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

If you are testing the automatic flush on connectivity restoration rather than an explicit one, you need the stream to emit — reach for your mocking library's observable support, or a `Subject<bool>` from `System.Reactive` in the test project if you're happy with that dependency.

And if a test is simply online throughout, `AlwaysOnlineConnectivityService` is already the fake you want.

## Replaying without a network

`options.ReplayTransport` is the transport a replay falls back to when its originating pipeline cannot be resolved. Supplying a stub is how you exercise a flush without network access:

```csharp
services.AddHyperwyc(options =>
{
    options.ReplayTransport = new StubHandler();   // yours
});
```

Hyperwyc never disposes it; one you provide stays yours to dispose. See [Pipeline placement](pipeline.md#replays-and-replaytransport) for when the fallback is used at all, which in a correctly-registered application is never.

## Testing the offline paths without faking anything

The tutorial's approach works in an integration test too, and it exercises the real code path rather than a mocked one: **stop the API**. A refused connection is a transport failure, which is what actually drives Hyperwyc's offline behaviour — a read falls back to the store and a write is queued, without any connectivity service being involved.

That is worth preferring where you can, because it is the path your application will really take. A connectivity service that reports offline is the *optimisation*; the transport failing is the mechanism. See [Design](design.md#connectivity-is-an-optimisation-not-a-correctness-input).

## A store per test

The store is durable, which is the point, and that makes it shared state between tests. Give each one its own directory:

```csharp
services.AddHyperwyc(configureStore: store =>
{
    store.DirectoryPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
});
```

Or use `InMemoryStore` through `AddHyperwycCore<InMemoryStore>()` where durability is not what you are testing.
