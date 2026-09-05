# Getting started

Install it, register it, and make a request. Nothing about your existing `HttpClient` code changes.

```bash
dotnet add package Hyperwyc
```

```csharp
services.AddHttpClient("MyApi")
    .AddHyperwycHandler()
    .AddHttpMessageHandler<AuthHandler>();

services.AddHyperwyc();
```

That's the whole setup. You get a durable, encrypted store and a working connectivity source
without deciding anything.

**On a mobile device, add one more line.** Hyperwyc falls back to a BCL connectivity check that
reports whether a network interface is up, not whether your API is reachable — good enough on a
desktop or a server, wrong often enough on a phone to be worth replacing:

```csharp
services.AddSingleton<IConnectivityService, MauiConnectivityService>();
```

That is an optimisation, not a repair. Nothing is lost when the fallback is wrong: a read is
served from the store and a write is queued, exactly as if the device had been known to be
offline. What you save is a doomed request each time. [Connectivity](connectivity.md) has an
implementation to copy for MAUI and for Windows.

Everything else has a working default, and is there when you want it:

```csharp
services.AddHyperwyc(options =>
{
    options.Routes.Default = RoutePolicy.CacheFirst(TimeSpan.FromDays(1));
});
```

## Packages

| Package | Use it when |
|---|---|
| `Hyperwyc` | Almost always. Includes durable [Cabinet](https://github.com/mattgoldman/cabinet)-backed storage and works with no configuration |
| `Hyperwyc.Core` | You are supplying your own `IHyperwycStore`. No storage dependency; call `AddHyperwycCore<TStore>()` instead |

The store is a type parameter on `AddHyperwycCore<TStore>()` rather than a setting, so forgetting it is a compile error rather than a silent fall back to in-memory storage that loses everything on restart:

```csharp
services.AddHyperwycCore<MyCustomStore>();          // container constructs it
services.AddHyperwycCore(sp => new MyStore(...));   // or supply a factory
```
