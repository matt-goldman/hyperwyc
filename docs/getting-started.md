# Getting started

Install the package, register it, and make a request. Nothing about your existing `HttpClient` code changes.

```bash
dotnet add package Hyperwyc
```

```csharp
services.AddHttpClient("MyApi")
    .AddHyperwycHandler() // Add the handler to the HttpClient
    .AddHttpMessageHandler<AuthHandler>();   // yours, if you have one

services.AddHyperwyc(); // Register the Hyperwyc dependencies
```

That's the whole setup. You get a durable, encrypted store and a working connectivity source without deciding anything.

**Register `AddHyperwycHandler()` first**, before your own handlers. Everything after it also runs on replayed requests, which is how a write queued on Monday but sent on Tuesday goes out with Tuesday's token. See [Pipeline placement](pipeline.md) for a full explanation of why (and when not to).

## Your calls are untouched

```csharp
// Online: fetched, and cached on the way back.
// Offline: served from the cache, from this same line.
var products = await client.GetFromJsonAsync<List<Product>>("/products");

// Online: sent normally.
// Offline: queued durably, and you get 202 Accepted.
var response = await client.PostAsJsonAsync("/sales", sale);
```

Neither call knows Hyperwyc is there: a read that cannot be served returns `null` rather than throwing, and a write that cannot be sent is kept and replayed when the network returns.

## On a mobile device, add one more line

```csharp
services.AddSingleton<IConnectivityService, MauiConnectivityService>();
```

`MauiConnectivityService` is a class you copy into your app. It's about twenty lines, available in [Hyperwyc in a .NET MAUI app](maui.md), along with three other things a .NET MAUI app should do.

**This is an optimisation, rather than a a repair.** Without it Hyperwyc falls back to a BCL check that reports whether a network interface is up, which errs toward "connected", so a request is attempted, and if the transport fails, a read is served from the store while a write is queued, exactly as if the device had been known to be offline. What you gain is short-cutting a doomed request, and, more importantly, a notification signal when the network returns, because that is what sends queued writes. See [Connectivity](connectivity.md).

## Customising

Everything else has a working default:

```csharp
services.AddHyperwyc(options =>
{
    options.Routes.Default = RoutePolicy.CacheFirst(TimeSpan.FromDays(1));
});
```

[Delivery and route policies](delivery.md) covers the rest.

## Packages

| Package         | Use it when                                                                                                                      |
| --------------- | -------------------------------------------------------------------------------------------------------------------------------- |
| `Hyperwyc`      | Almost always. Includes durable [Cabinet](https://github.com/mattgoldman/cabinet)-backed storage and works with no configuration |
| `Hyperwyc.Core` | You are supplying your own `IHyperwycStore`. No storage dependency; call `AddHyperwycCore<TStore>()` instead                     |

The store is a type parameter on `AddHyperwycCore<TStore>()` rather than a setting, so forgetting it is a compile error rather than a silent fall back to in-memory storage that loses everything on restart:

```csharp
services.AddHyperwycCore<MyCustomStore>();          // container constructs it
services.AddHyperwycCore(sp => new MyStore(...));   // or supply a factory
```

## Next

- **[Tutorial](tutorial/)** — the same thing built up slowly, with an API you can stop to see what
  happens.
- **[Hyperwyc in a .NET MAUI app](maui.md)** — if that is what you are building.
- **[Design](design.md)** — why any of it behaves the way it does.
