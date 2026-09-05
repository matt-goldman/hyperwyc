# Getting started

Install it, register it, and make a request. Nothing about your existing `HttpClient` code changes.

```bash
dotnet add package Hyperwyc
```

```csharp
services.AddSingleton<IConnectivityService, MyConnectivityService>();

services.AddHttpClient("MyApi")
    .AddHyperwycHandler()
    .AddHttpMessageHandler<AuthHandler>();

services.AddHyperwyc();
```

That's the whole setup. Storage needs no decision — `AddHyperwyc()` gives you a durable,
encrypted store out of the box.

The one thing Hyperwyc can't decide for you is how to tell whether the device is online, so
you supply an `IConnectivityService`. Register it like any other service and you're done; if
you don't have an implementation, [Connectivity](connectivity.md) covers your options, one of
which ships in the box.

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
