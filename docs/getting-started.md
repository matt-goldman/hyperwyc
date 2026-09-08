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

That's the whole setup. You get a durable, encrypted store and a working connectivity source without deciding anything.

[comment: Accurate, but worth one clause on which way it errs, because that is the entire reason it is safe to default at all (ADR 0007). Without it this reads as "we picked one for you", which is exactly what ADR 0006 says shipping an implementation is not.]

**On a mobile device, add one more line.** Hyperwyc falls back to a BCL connectivity check that reports whether a network interface is up, not whether your API is reachable; good enough on a desktop or a server, wrong often enough on a phone to be worth replacing:

```csharp
services.AddSingleton<IConnectivityService, MauiConnectivityService>();
```

[comment: MauiConnectivityService does not ship in any package - it is a class the reader copies out of connectivity.md. As written this looks like a type they already have, so their first build fails. It needs "copy this class from Connectivity" right here, or the MAUI quick start to own it.]

That is an optimisation, not a repair. The fallback errs toward reporting connected, which if wrong just costs the extra time it takes for a request to fail. A read is then served from the store and a write is queued, exactly as if the device had been known to be offline. A more robust implementation saves you the time it takes for the request to fail each time, which on a phone is worth saving. [Connectivity](connectivity.md) has a reference implementation to copy for .NET MAUI and for Windows.

[comment: This gives the weaker of the two reasons to supply your own. The stronger one is that ConnectivityChanged is what drains the outbox - connectivity.md:28 says exactly this, and it does not make it into Getting started. A queued write on a device with a poor connectivity signal does not just go out late; it may not go out until the next launch.]

Everything else has a working default, and is customisable when you want to:

```csharp
services.AddHyperwyc(options =>
{
    options.Routes.Default = RoutePolicy.CacheFirst(TimeSpan.FromDays(1));
});
```

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

[comment: The opening line promises "install it, register it, and make a request" and there is no request on this page. The README has the two-call example; this does not. More generally: right now Getting started is the README's registration block plus a packages table that is also in the README, worded differently in each. Whichever way the tutorial goes, one of these two should stop existing in its current form.]
