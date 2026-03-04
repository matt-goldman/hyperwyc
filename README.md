# Restyc

> **Core Philosophy:** Restyc removes the complexity of handling online/offline state from your application logic. Whether you're connected or not, requests are sent when possible and responses are returned when possible — without requiring you to change anything else in your app. You can control caching and replay rules if you want, or just enjoy sensible defaults. This makes it ideal for scenarios where the likelihood of data conflicts is low, or where you already have your own resolution logic in place.

Restyc is a backend-agnostic, HTTP-based resilience layer for .NET applications. It intercepts API calls made via `HttpClient`, persists them locally, and ensures delivery or refresh once connectivity is available. It provides reliable offline support and cache-aware fetch semantics without dictating how you structure your app, models, or storage.

---

## Features

- ✅ Backend-agnostic HTTP caching and replay layer (REST/JSON over HTTP/1.x; v1.0)
- ✅ Offline request queue with retry
- ✅ Idempotency-Key injection on all mutating requests
- ✅ Response cache with expiry policies
- ✅ Write-triggered GET cache invalidation
- ✅ Pluggable policies (connectivity, staleness, retry)
- ✅ Observables for sync lifecycle events
- ✅ Works with any `HttpClient`, minimal blast radius

---

## Quick Start

```bash
dotnet add package Restyc.Cabinet
```

`Restyc.Cabinet` depends on `Restyc`, so the core arrives transitively. To use a different store, or implement your own, install only `Restyc`.

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

---

## When to Use

- You need offline resilience in a .NET app
- You want API calls to work seamlessly online or offline
- You want transport-level durability, not a storage-first sync framework
- Your app already has a stable API contract and you don't want to rearchitect around a sync engine

## What It Doesn't Do

- Doesn't handle auth or token refresh (your own handler should — place it after `RestycHandler`)
- Doesn't dictate your data model
- Doesn't replace your local database
- Doesn't resolve data conflicts — it's designed for scenarios where conflicts are rare or handled server-side

---

## How It Works

Restyc sits in your `HttpClient` pipeline as a `DelegatingHandler`. It transparently intercepts all outgoing requests:

- **Online:** Requests are sent immediately. Responses are optionally cached according to your staleness policy.
- **Offline:** Requests are serialised and queued locally. When connectivity is restored, they are replayed in order.
- **Read requests (GET/HEAD/OPTIONS):** Served from cache if available and fresh; fetched from the API if stale or missing.

The app doesn't need to know the difference. Your existing code doesn't change.

---

## Auth Handler Placement

Restyc does not manage authentication. When placing handlers, order matters:

- **Expiring tokens** (e.g. OAuth/JWT): Place `RestycHandler` **before** your auth handler so that replayed requests pick up fresh tokens.
- **Non-expiring tokens** (e.g. API keys): Order is flexible.

```csharp
// Correct order for expiring auth:
.AddHttpMessageHandler<RestycHandler>()   // queues and replays
.AddHttpMessageHandler<AuthHandler>()     // adds fresh token at send time
```

---

## Lifecycle Events

Subscribe to `IObservable<SyncEvent>` to observe state changes:

```csharp
restyc.SyncEvents.Subscribe(e => Console.WriteLine($"{e.Type}: {e.Url}"));
```

| Event | Meaning |
|-------|---------|
| `OnQueued` | Request persisted to local queue (offline) |
| `OnRetrying` | Retry attempt initiated |
| `OnSynced` | Request successfully delivered |
| `OnFailed` | Request moved to dead-letter after max retries |
| `OnUpdated` | Cached response refreshed |

---

## Further Reading

- [TECHNICAL_PLAN.md](TECHNICAL_PLAN.md) — Architecture, components, storage model, and design rationale
- [ROADMAP.md](ROADMAP.md) — Milestones and planned features
- [POC.md](POC.md) — Sample application and proof-of-concept setup
