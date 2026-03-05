# Restyc

> **A service-worker-inspired HTTP handler for .NET** — your app code never needs to know whether it's online or offline.

Restyc sits in the `HttpClient` pipeline and transparently handles caching, queuing, and replay. Like a [Service Worker](https://developer.mozilla.org/en-US/docs/Web/API/Service_Worker_API) in a PWA, it intercepts outgoing HTTP requests and returns normal-looking responses regardless of connectivity. Your existing `HttpClient` code doesn't change. The `X-Restyc-Status` header is always present on synthetic responses for code that *wants* to know.

Restyc is backend-agnostic, storage-pluggable, and designed for scenarios where data conflicts are rare or handled server-side.

---

## Features

- ✅ **Service-worker-inspired** — transparent 200 OK responses by default; callers never branch on connectivity
- ✅ Backend-agnostic HTTP caching and replay layer (REST/JSON over HTTP/1.x; v1.0)
- ✅ Offline request queue with retry
- ✅ Idempotency-Key injection on all mutating requests
- ✅ Response cache with expiry policies
- ✅ Write-triggered GET cache invalidation
- ✅ Configurable offline response policy (transparent 200 or explicit 503)
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

- You want a **service-worker-like** drop-in resilience layer for .NET HTTP clients
- You need offline resilience without rewriting your app around a sync framework
- You want API calls to look and feel the same online or offline
- You want transport-level durability, not a storage-first sync engine
- Your app already has a stable API contract and you don't want to rearchitect

## What It Doesn't Do

- Doesn't handle auth or token refresh (your own handler should — place it after `RestycHandler`)
- Doesn't dictate your data model
- Doesn't replace your local database
- Doesn't resolve data conflicts — it's designed for scenarios where conflicts are rare or handled server-side

---

## How It Works

Restyc sits in your `HttpClient` pipeline as a `DelegatingHandler` — the same interception point that a Service Worker occupies for browser `fetch()`. It transparently handles all outgoing requests:

- **Online:** Requests are sent immediately. Responses are optionally cached according to your staleness policy.
- **Offline writes:** Requests are serialised and queued locally. The caller receives a `200 OK` (by default) with an `X-Restyc-Status: Queued` header. When connectivity is restored, the queue is replayed in order.
- **Offline reads:** Served from cache if available (even if stale — any data is better than no data offline). If no cache exists, the caller receives a `200 OK` with `X-Restyc-Status: Offline`.
- **Online reads (GET/HEAD/OPTIONS):** Served from cache if fresh; fetched from the API if stale or missing.

The app doesn't need to know the difference. Your existing code doesn't change.

> **Opt-in signalling:** Set `OfflineResponsePolicy = OfflineResponsePolicy.Signal` to return `503 Service Unavailable` instead, for routes where your app needs to handle the offline state explicitly. Per-route policies are planned for v1.0.

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
