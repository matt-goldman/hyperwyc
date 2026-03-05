# Issue 15 — `AddRestyc()` DI Extension and `RestycOptions` Configuration

## Summary

Implement the `AddRestyc()` extension method for `IServiceCollection` and the `RestycOptions` configuration object, so consumers can register and configure Restyc in a standard .NET DI container.

## Background

Restyc integrates via `IHttpClientFactory`. The `AddRestyc()` extension makes it easy to register the handler and configure policies in one place.

## Target API

```csharp
services.AddHttpClient("MyApi")
    .AddHttpMessageHandler<RestycHandler>()
    .AddHttpMessageHandler<AuthHandler>();

services.AddRestyc(options =>
{
    options.DefaultPolicy = SyncPolicy.CacheFirst(TimeSpan.FromDays(1));
    options.Store = new CabinetSyncStore("restyc.db");
    options.Connectivity = new MauiConnectivityService();
});
```

## `RestycOptions` Properties

| Property | Type | Default | Description |
|---|---|---|---|
| `DefaultPolicy` | `ISyncPolicy` | `SyncPolicy.CacheFirst(1 day)` | Default cache/retry policy |
| `Store` | `ISyncStore` | `InMemorySyncStore` | Store implementation |
| `Connectivity` | `IConnectivityService` | `AlwaysOnlineConnectivityService` | Connectivity source |
| `StalenessEvaluator` | `IStalenessEvaluator` | `TtlStalenessEvaluator` | Determines cache freshness |
| `MaxCachedResponseBodyBytes` | `int` | `524288` (512 KB) | Max body size to cache |
| `ConnectivityDebounceDelay` | `TimeSpan` | `2 seconds` | Debounce delay on connectivity restore |
| `FlushOnStartup` | `bool` | `true` | Trigger sync flush on app start |

## `SyncPolicy` Factory

Provide a `SyncPolicy` static class for common policy presets:

```csharp
SyncPolicy.CacheFirst(TimeSpan ttl)
SyncPolicy.ApiFirst()
SyncPolicy.CacheOnly()
SyncPolicy.NetworkOnly()
```

## Acceptance Criteria

- [x] `AddRestyc(Action<RestycOptions>? configure = null)` extension method on `IServiceCollection`.
- [x] `RestycOptions` with all properties above and documented defaults.
- [x] `RestycHandler` registered as a transient `DelegatingHandler`.
- [x] `IRestyc`, `SyncEventStream`, and `SyncOrchestrator` registered as singletons.
- [x] `ISyncStore`, `IConnectivityService`, `IStalenessEvaluator`, `ISyncPolicy` resolved from the options object.
- [x] `SyncPolicy` static factory with `CacheFirst`, `ApiFirst`, `CacheOnly`, `NetworkOnly` presets.
- [x] Unit tests verify correct registration and `IRestyc` resolution.

## Notes

- `AddRestyc()` must not throw if called without configuring a store — `InMemorySyncStore` is the safe fallback.
- If `FlushOnStartup = true`, the orchestrator's flush is triggered from `IHostedService.StartAsync` or an equivalent startup hook.
- `SyncPolicy.CacheFirst(TimeSpan)` returns an internal `PresetSyncPolicy` record that carries the TTL. `AddRestyc()` detects this type and propagates the TTL to `DefaultCacheTtl` automatically, keeping the staleness evaluator in sync.
- `ServiceCollectionExtensions` uses `Microsoft.Extensions.DependencyInjection.Abstractions` (not the full DI package) to stay lightweight. Callers provide their own DI container.
- `RestycHostedService` is registered via `AddHostedService<RestycHostedService>()` only when `FlushOnStartup = true`, so it is a no-op in scenarios that manage flushing manually.
- 13 new unit tests added in `ServiceCollectionExtensionsTests.cs`; total test count: 138.
