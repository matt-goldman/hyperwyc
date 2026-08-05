# Hyperwyc

> **A service-worker-inspired HTTP handler for .NET** — your app code never needs to know whether it's online or offline.

Hyperwyc sits in the `HttpClient` pipeline and transparently handles caching, queuing, and replay. Like a [Service Worker](https://developer.mozilla.org/en-US/docs/Web/API/Service_Worker_API) in a PWA, it intercepts outgoing HTTP requests and returns normal-looking responses regardless of connectivity. Your existing `HttpClient` code doesn't change. The `X-Hyperwyc-Status` header is always present on synthetic responses for code that *wants* to know.

Hyperwyc is backend-agnostic, storage-pluggable, and designed for scenarios where data conflicts are rare or handled server-side.

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
dotnet add package Hyperwyc
```

```csharp
services.AddHttpClient("MyApi")
    .AddHttpMessageHandler<HyperwycHandler>()
    .AddHttpMessageHandler<AuthHandler>();

services.AddHyperwyc();
```

That's the whole setup. `AddHyperwyc()` with no arguments gives you durable, encrypted storage — no store to choose, nothing to wire up. Configure it when you want to:

```csharp
services.AddHyperwyc(options =>
{
    options.DefaultPolicy = SyncPolicy.CacheFirst(TimeSpan.FromDays(1));
    options.Connectivity = new MauiConnectivityService();   // see Connectivity, below
});
```

### Packages

| Package | Use it when |
|---|---|
| `Hyperwyc` | Almost always. Includes durable [Cabinet](https://github.com/mattgoldman/cabinet)-backed storage and works with no configuration |
| `Hyperwyc.Core` | You are supplying your own `ISyncStore`. No storage dependency; call `AddHyperwycCore<TStore>()` instead |

The store is a type parameter on `AddHyperwycCore<TStore>()` rather than a setting, so forgetting it is a compile error rather than a silent fall back to in-memory storage that loses everything on restart:

```csharp
services.AddHyperwycCore<MyCustomStore>();          // container constructs it
services.AddHyperwycCore(sp => new MyStore(...));   // or supply a factory
```

### Storage location and encryption

By default the store lives in a `Hyperwyc` folder under `LocalApplicationData` — inside the app sandbox on Android and iOS — and is encrypted with AES-256-GCM using a key derived from that path.

That default costs you nothing and keeps cached data from casual inspection of the device filesystem, but the derived key is deterministic, so it is not a defence against an attacker who has the device and knows what this library does. If the cached data warrants more, supply your own key:

```csharp
services.AddHyperwyc(configureStore: store =>
{
    store.DirectoryPath = myPath;
    store.EncryptionKey = keyFromSecureStorage;   // 32 bytes
});
```

Losing that key means losing access to everything already stored.

---

## When to Use

- You want a **service-worker-like** drop-in resilience layer for .NET HTTP clients
- You need offline resilience without rewriting your app around a sync framework
- You want API calls to look and feel the same online or offline
- You want transport-level durability, not a storage-first sync engine
- Your app already has a stable API contract and you don't want to rearchitect

## What It Doesn't Do

- Doesn't handle auth or token refresh (your own handler should — place it after `HyperwycHandler`)
- Doesn't dictate your data model
- Doesn't replace your local database
- Doesn't resolve data conflicts — it's designed for scenarios where conflicts are rare or handled server-side

---

## How It Works

Hyperwyc sits in your `HttpClient` pipeline as a `DelegatingHandler` — the same interception point that a Service Worker occupies for browser `fetch()`. It transparently handles all outgoing requests:

- **Online:** Requests are sent immediately. Responses are optionally cached according to your staleness policy.
- **Offline writes:** Requests are serialised and queued locally. The caller receives a `202 Accepted` (by default) with an `X-Hyperwyc-Status: Queued` header. When connectivity is restored, the queue is replayed in order. `202` is used rather than `200` because the request has been accepted for later processing but not yet performed against the origin server — once sync-status inspection lands, callers will be able to confirm the eventual outcome.
- **Offline reads:** Served from cache if available (even if stale — any data is better than no data offline). If no cache exists, the caller receives a `200 OK` with `X-Hyperwyc-Status: Offline` and an empty body.
- **Online reads (GET/HEAD/OPTIONS):** Served from cache if fresh; fetched from the API if stale or missing.

### Caching strategies

The default is cache-first. Set `options.DefaultPolicy` to choose a different one:

| Policy | Behaviour |
|---|---|
| `SyncPolicy.CacheFirst()` | Serve a fresh cached response; otherwise fetch. Uses `DefaultCacheTtl` |
| `SyncPolicy.CacheFirst(ttl)` | As above, with the freshness window stated on the policy |
| `SyncPolicy.ApiFirst()` | Always fetch; fall back to the cache only if the request fails |
| `SyncPolicy.CacheOnly()` | Serve from cache regardless of age; never touch the network |
| `SyncPolicy.NetworkOnly()` | Always fetch; never read or write the cache |

Offline, `CacheFirst` and `ApiFirst` both serve stale cached data rather than nothing, and
`CacheOnly` behaves the same as it does online. `NetworkOnly` opts out of the cache entirely,
so it has nothing to offer offline.

A `CacheOnly` read that finds nothing cached returns `X-Hyperwyc-Status: CacheMiss` rather than
`Offline` — the device may be online, and the request was withheld by policy, not connectivity.

The app doesn't need to know the difference. Your existing code doesn't change.

> **Why "no data" instead of "no connection"?** Connectivity is an infrastructure concern, not an application one. Your code already has to handle the empty-result path (a search with no matches, a feed with no items); offline simply produces the same shape. If that mindset shift doesn't fit a particular route, set `OfflineResponsePolicy = OfflineResponsePolicy.Signal` to receive `503 Service Unavailable` instead. Per-route policies are planned for v1.0.

### Designing your responses

Transparent offline reads can return a `200 OK` with an empty body. How (and whether) that affects your code depends on how you deserialise responses:

- **Using `HttpClientJsonExtensions` (e.g. `GetFromJsonAsync<T>`, `ReadFromJsonAsync<T>`).** The empty body throws a `JsonException` from inside the extension, so the call site must wrap the whole chain in a `try`/`catch` that distinguishes "deserialisation failure" from "genuine API error" — typically by also re-checking the HTTP status code, which the extension has already discarded. This is awkward, so the recommended approach is to adopt an **envelope or result pattern** in your API responses (see for example [`Ardalis.Result`](https://github.com/ardalis/Result), or a small hand-rolled `ApiResponse<T>`). An empty body then deserialises to `null` or a default, which application code can handle uniformly online and offline.
- **Calling `SendAsync` / `GetAsync` and deserialising the response yourself.** No library change is needed: wrap just the deserialisation step in a `try`/`catch` (or check `Content.Headers.ContentLength`) and treat "no body" as "no data". The envelope pattern is still a nice-to-have but no longer load-bearing.
- **Per-route opt-out.** If neither option fits a particular route, set `OfflineResponsePolicy.Signal` on it and branch on `503`. Per-route policies are planned for v1.0.

A future per-route option will let Hyperwyc return a caller-supplied default body (e.g. `"[]"`) on offline reads so that even `GetFromJsonAsync<List<T>>` works without an envelope — tracked in the roadmap.

### Current limitations

- **Text bodies only.** v0.1 handles string request and response bodies. Binary payloads (file uploads, image downloads, protobuf, etc.) are on the roadmap.

---

## Auth Handler Placement

Hyperwyc does not manage authentication. When placing handlers, order matters:

- **Expiring tokens** (e.g. OAuth/JWT): Place `HyperwycHandler` **before** your auth handler so that replayed requests pick up fresh tokens.
- **Non-expiring tokens** (e.g. API keys): Order is flexible.

```csharp
// Correct order for expiring auth:
.AddHttpMessageHandler<HyperwycHandler>()   // queues and replays
.AddHttpMessageHandler<AuthHandler>()     // adds fresh token at send time
```

> **Note — Hyperwyc short-circuits the pipeline when offline.** Synthetic responses (`Queued`, `Offline`) are returned directly from the handler, so any `DelegatingHandler` placed *after* `HyperwycHandler` is **not** invoked on the offline path. This is by design — there is no outbound request to authenticate or otherwise mutate — but it means downstream handlers should not be relied upon for side effects that need to occur on every logical request (logging, telemetry, header stamping). For cross-cutting concerns that must run regardless of connectivity, place the handler **before** `HyperwycHandler` in the pipeline.

---

## Lifecycle Events

Subscribe to `IObservable<SyncEvent>` to observe state changes:

```csharp
Hyperwyc.SyncEvents.Subscribe(e => Console.WriteLine($"{e.Type}: {e.Url}"));
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

- [TECHNICAL_PLAN.md](TECHNICAL_PLAN.md) — How Hyperwyc works today: architecture, components, storage model, and design rationale
- [ROADMAP.md](ROADMAP.md) — Where it's going: milestones and planned features
- [Backlog/README.md](Backlog/README.md) — Per-item status, priority, and dependencies
- [POC.md](POC.md) — Sample application and proof-of-concept setup
