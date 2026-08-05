# Hyperwyc — Technical Plan

**What Hyperwyc does today, and why it is built that way.** This document describes the
shipped architecture. Anything not yet built lives in [ROADMAP.md](ROADMAP.md) and the
[backlog index](Backlog/README.md); where current behaviour falls short of the intended
design, this document says so and links to the item that closes the gap.

## Overview

Hyperwyc is a service-worker-inspired HTTP handler for .NET. Like a [Service Worker](https://developer.mozilla.org/en-US/docs/Web/API/Service_Worker_API)
in a progressive web app, it intercepts outgoing HTTP requests and returns normal-looking
responses to the caller regardless of connectivity state. Requests are cached, queued, and
replayed transparently — the consuming code never needs to branch on online/offline status.

Its core design prioritises invisibility: offline writes return `202 Accepted` by default
(with an `X-Hyperwyc-Status: Queued` header for code that wants to know), offline reads with
no cached data return `200 OK` with an empty body, and cached reads are served with their
originally cached status codes. An opt-in `OfflineResponsePolicy.Signal` mode returns `503`
for applications that need explicit offline handling.

---

## Core Principles

1. **Transparent by default** — Like a Service Worker, the handler is invisible to callers.
   Responses look normal regardless of connectivity; the `X-Hyperwyc-Status` header is the
   opt-in escape hatch.
2. **Transport-level durability** — Operates at the HTTP layer, not the data model layer.
3. **Backend agnostic** — Works with any REST API over HTTP/1.x. GraphQL (mutation-vs-query
   ambiguity on POST) and gRPC (binary framing, HTTP/2 semantics) are deferred.
4. **Storage pluggability** — The core package ships with interfaces only; store providers
   are separate, explicitly installed packages.
5. **No architectural imposition** — Developers do not need to alter their domain models or
   API design.
6. **Composability** — Integrates via `DelegatingHandler` and works alongside other handlers
   such as auth or logging.
7. **Predictable behaviour** — Requests sent when possible, responses returned when possible,
   following cache and retry policies.

---

## Package Structure

| Package | Assembly | Contents |
|---------|----------|----------|
| `Hyperwyc.Core` | `Hyperwyc.Core.dll` | `HyperwycHandler`, all interfaces (`ISyncStore`, `IConnectivityService`, `ISyncPolicy`, `IStalenessEvaluator`), `SyncEventStream`, `SyncOrchestrator`, `IHyperwyc`, `InMemorySyncStore`, `AlwaysOnlineConnectivityService`, `TtlStalenessEvaluator`. Depends on `Microsoft.Extensions.DependencyInjection.Abstractions`, `Microsoft.Extensions.Hosting.Abstractions` and `Polly`; no storage dependency. |
| `Hyperwyc` | `Hyperwyc.dll` | `CabinetSyncStore` backed by [Cabinet](https://github.com/mattgoldman/cabinet), `CabinetStoreOptions`, and the batteries-included `AddHyperwyc()`. Depends on `Hyperwyc.Core` and `Cabinet`. |

`Hyperwyc` is the package almost everyone installs: `AddHyperwyc()` with no arguments produces
a working, durable configuration. `Hyperwyc.Core` is for developers supplying their own
`ISyncStore`, and future provider packages — `Hyperwyc.LiteDb`, `Hyperwyc.IndexedDb` — sit
alongside it.

Package ID, assembly name and root namespace are deliberately independent. `Hyperwyc.Core`
ships types in the `Hyperwyc` namespace, and `Hyperwyc` ships `CabinetSyncStore` in
`Hyperwyc.Cabinet`, so a single `using Hyperwyc;` reaches the common surface regardless of
which packages are installed.

### Registration

| Entry point | Package | Store |
|---|---|---|
| `AddHyperwyc(configure?, configureStore?)` | `Hyperwyc` | `CabinetSyncStore`, constructed by the container |
| `AddHyperwycCore<TStore>(configure?)` | `Hyperwyc.Core` | `TStore`, constructed by the container |
| `AddHyperwycCore(storeFactory, configure?)` | `Hyperwyc.Core` | Whatever the factory returns |

The store is a type parameter rather than a property on `HyperwycOptions`. That makes omitting
it a compile-time error, where an options property would allow an application to silently run
on `InMemorySyncStore` and lose every queued write at restart — the exact failure the library
exists to prevent. Registration is by type, so the container owns construction and disposal and
no store is built (and no directory touched) unless something resolves it.

---

## Technical Design

### 1. Pipeline Integration

- **Entry point:** `HyperwycHandler : DelegatingHandler`, registered transient so each named
  client pipeline gets its own instance.
- **Usage:** Added to the `HttpClient` pipeline via `IHttpClientFactory`.
- **Processing order:**
  - Place `HyperwycHandler` **before** authentication handlers for expiring tokens, so
    replayed requests pick up fresh tokens.
  - For non-expiring auth (e.g. API keys), order is flexible.
  - Handlers placed *after* `HyperwycHandler` are not invoked on the offline path, because
    synthetic responses short-circuit the pipeline. Cross-cutting concerns that must run on
    every logical request belong before it.

### 2. Request Handling Logic

The handler branches first on connectivity, then on whether the method is mutating
(`POST`, `PUT`, `PATCH`, `DELETE`) or not.

#### Outgoing Requests (Write Operations)

1. An `Idempotency-Key` header is injected on every mutating request unless the caller
   supplied one. The same key is reused on replay, enabling server-side duplicate
   suppression. The envelope's `Id` is taken from this key so the two never diverge.
2. The handler checks connectivity via `IConnectivityService`.
3. If **offline**:
   - Buffers and serialises the request into an envelope.
   - Persists it with `IsSynced = false`.
   - Returns a synthetic `202 Accepted` (`OfflineResponsePolicy.Transparent`, the default)
     or `503 Service Unavailable` (`Signal`), always with `X-Hyperwyc-Status: Queued`.
   - Publishes `OnQueued`.
4. If **online**:
   - Sends the request immediately.
   - On success, invalidates cached GETs sharing the URL prefix (configurable via
     `ISyncPolicy.ShouldInvalidateCacheOnWrite`), and publishes `OnSynced`.

Online writes are not persisted to the outbox — they either succeed against the origin or
their failure status is returned to the caller unchanged. Only offline writes are queued.

#### Incoming Responses (Read Operations)

Read handling for `GET`, `HEAD` and `OPTIONS` is governed by the `CacheStrategy` that
`ISyncPolicy.GetStrategy(request)` returns for the request:

| Strategy | Online | Offline |
|---|---|---|
| `CacheFirst` (default) | Serves a fresh cached response; otherwise fetches, caches and returns | Serves the cached response even if stale; otherwise a synthetic offline response |
| `ApiFirst` | Always fetches; falls back to the cache only if the request throws | Serves the cached response even if stale; otherwise a synthetic offline response |
| `CacheOnly` | Serves the cached response regardless of staleness; never sends | Same as online — the network is never consulted either way |
| `NetworkOnly` | Always sends; never reads or writes the cache | Synthetic offline response; the cache is not consulted |

Successful responses are cached subject to the body size cap, and publish `OnUpdated`. Cached
responses are reconstructed with their original status code and headers.

`ApiFirst`'s fallback triggers on `HttpRequestException` — the case where
`IConnectivityService` reports online but the API is not actually reachable (captive portal,
DNS failure, transient outage). `CacheFirst` deliberately does *not* fall back this way: it has
already considered the cache and judged it stale.

Synthetic read responses carry `X-Hyperwyc-Status: Offline`, except a `CacheOnly` read that
finds nothing cached, which carries `CacheMiss` — the device may well be online, and the
request was withheld by policy rather than by connectivity.

#### Cache Invalidation Prefix

`DeriveInvalidationPrefix` strips a trailing path segment when it parses as a `long` or a
`Guid`, so a write to `/api/notes/42` invalidates the cached `/api/notes` collection as well
as the individual resource. Query strings are excluded from the prefix.

---

### 3. Synchronisation Workflow

`SyncOrchestrator` owns outbound replay. It subscribes to
`IConnectivityService.ConnectivityChanged` at construction and exposes `FlushAsync` for
manual triggering (e.g. a "sync now" button).

- On connectivity restored, events are debounced (default 2 seconds) before a flush is
  triggered. A `SemaphoreSlim` admits one flush at a time; concurrent calls return
  immediately rather than queueing.
- `HyperwycHostedService` triggers a flush at startup when `FlushOnStartup` is set and the
  device is online.
- Envelopes are replayed in stored order via a bare transport handler, bypassing
  `HyperwycHandler` so replays are not re-queued.
- On successful delivery: marks `IsSynced = true`, publishes `OnSynced`, and applies
  write-triggered invalidation.
- On failure:
  - Failure means a non-2xx response or a transport exception — not a connectivity-state
    change. A device that `IConnectivityService` considers online but that cannot reach the
    API (captive portal, DNS failure, transient outage) is handled by the retry budget, not
    by the offline queue.
  - Polly retries with exponential backoff and jitter, publishing `OnRetrying` per attempt.
  - After the retry budget is exhausted: moves to dead-letter, publishes `OnFailed`.

> **In-process only:** the retry budget lives in the Polly pipeline for the duration of a
> single flush. `Envelope.RetryCount`, `Envelope.NextRetryUtc` and
> `ISyncStore.GetDueForRetryAsync` exist and are tested, but the orchestrator does not write
> or read them, so an interrupted flush restarts the budget. Tracked in
> [issue 28](Backlog/28-persisted-retry-state.md).

#### What triggers a flush

Only two things: application startup, when `FlushOnStartup` is set and the device is online;
and connectivity being restored while the app runs. Plus `FlushAsync` itself, for a manual
"sync now" affordance.

**Shutdown is deliberately not a trigger**, on any platform. Envelopes reach the outbox only
via the offline write path, so a non-empty outbox means connectivity was poor — and shutting
down does not improve connectivity. Anything still queued is replayed at next start. On mobile
the question is moot regardless: Android and iOS terminate suspended processes without running
disposal at all.

#### Disposal

`SyncOrchestrator` implements both `IDisposable` and `IAsyncDisposable`, and both mean *stop
now*. Each cancels a lifetime token that every flush is linked to — including a manual
`FlushAsync()` that supplied no token of its own — so the flush loop exits at its next envelope
boundary, with any in-progress send and backoff delay cancelled. `DisposeAsync` additionally
waits for that unwinding to complete; `Dispose` returns immediately. Neither waits for queued
work to be sent.

A cancelled envelope is left untouched: it is neither marked synced nor dead-lettered, so it
remains in the outbox for the next start. Both paths are idempotent, and `FlushAsync` throws
`ObjectDisposedException` afterwards.

The flush semaphore and the `HttpMessageInvoker` are deliberately *not* disposed. A flush may
still be unwinding and would fault on either, surfacing an `ObjectDisposedException` on a
fire-and-forget task during shutdown; and neither holds a resource requiring release, since the
semaphore's `AvailableWaitHandle` is never used and the invoker does not own its transport.

---

### 4. Core Components

| Component | Responsibility |
|-----------|----------------|
| `HyperwycHandler` | Intercepts requests; routes to cache, network, or outbox. Pipeline entry point |
| `SyncOrchestrator` | Drains the outbox on connectivity restoration or manual flush; owns retry and dead-lettering |
| `HyperwycHostedService` | Triggers the startup flush |
| `HyperwycService` | Default `IHyperwyc` — exposes `SyncEvents` and `ResetStoreAsync` |
| `ISyncStore` | CRUD over stored envelopes; pluggable |
| `InMemorySyncStore` | Default non-durable store; the fallback when none is configured |
| `CabinetSyncStore` | Durable `ISyncStore` using Cabinet |
| `IConnectivityService` | Reports online/offline state and raises change events |
| `AlwaysOnlineConnectivityService` | Default implementation; always reports connected |
| `ISyncPolicy` | Cache strategy, write-invalidation rules, and retry configuration per request |
| `IStalenessEvaluator` | Determines whether a cached response is still fresh |
| `TtlStalenessEvaluator` | Default evaluator; stale once `CachedAt + TTL` has elapsed |
| `SyncEventStream` | Reactive event publisher (`IObservable<SyncEvent>`) |
| `HyperwycResponseFactory` | Builds the synthetic `Queued` and `Offline` responses |

---

### 5. Storage — Cabinet

Hyperwyc's default `ISyncStore` implementation uses **Cabinet**, whose document-oriented
model and flexible index system suit HTTP request/response envelopes. It ships in the
`Hyperwyc` package and is what `AddHyperwyc()` resolves with no configuration.

#### Key Advantages

- **Custom index providers** — envelopes are indexed by route, method, sync state or any
  metadata field.
- **Route-based indexing** — the request URL is the primary lookup key, enabling fast cached
  GET retrieval and selective replay of outbound writes.
- **Fits the envelope model** — each envelope is a standalone document; no schema rigidity.
- **Optimised for .NET** — pure managed code, mobile-safe, no native dependencies.
- **Encryption at rest** — AES-256-GCM via Cabinet's `AesGcmEncryptionProvider`.

This lets Hyperwyc answer efficiently:

- Get the cached response for `GET /api/notes`
- Get all unsynced outbound envelopes
- Get all envelopes due for retry
- Get all cached results for a given route prefix

#### Request/Response Envelope Schema

Each request/response pair is persisted as a single document:

```json
{
  "id": "guid",
  "url": "/api/notes",
  "method": "POST",
  "requestHeaders": {},
  "requestBody": "{...}",
  "isSynced": false,
  "isDeadLettered": false,
  "retryCount": 0,
  "nextRetryUtc": null,
  "createdUtc": "2025-10-11T10:00:00Z",
  "response": {
    "statusCode": 200,
    "headers": {},
    "body": "{...}",
    "cachedAt": "2025-10-11T10:00:10Z"
  }
}
```

#### Body Handling and Size Limits

Request and response bodies are stored as **strings**. Binary payloads — file uploads, image
downloads, protobuf — are read through `ReadAsStringAsync` and will not round-trip correctly;
this is the headline limitation of the current release, tracked in
[issue 25](Backlog/25-binary-request-response-bodies.md).

`HyperwycOptions.MaxCachedResponseBodyBytes` (default 524,288 — 512 KB) caps response
caching. Responses exceeding the limit are returned to the caller but not stored. Outbound
requests are queued regardless of body size.

---

### 6. Reactive Event Stream

Hyperwyc exposes sync lifecycle changes as `IObservable<SyncEvent>`:

```csharp
IObservable<SyncEvent> SyncEvents { get; }
```

`IObservable<T>` is a BCL interface (`System.IObservable<T>`). The core package implements it
with a minimal hand-rolled subject — no dependency on `System.Reactive`. Consumers who want
Rx operators (`.Where()`, `.Select()`, `.Throttle()`) add `System.Reactive` themselves. This
keeps the core dependency graph clean.

Plain .NET events are not exposed. `IObservable<T>` is strictly more capable; consumers who
prefer event-style consumption get it with a one-line `.Subscribe(...)`.

| Event | Trigger |
|-------|---------|
| `OnQueued` | Request cached to outbox (offline) |
| `OnRetrying` | Retry attempt initiated |
| `OnSynced` | Outbound request successfully delivered |
| `OnFailed` | Request moved to dead-letter after threshold |
| `OnUpdated` | Cached response refreshed from API |

---

### 7. Configuration

```csharp
services.AddHttpClient("MyApi")
    .AddHttpMessageHandler<HyperwycHandler>()
    .AddHttpMessageHandler<AuthHandler>();

services.AddHyperwyc();
```

`AddHyperwyc` builds a `HyperwycOptions` instance, applies the caller's delegate, and
registers the resolved services as singletons — except `HyperwycHandler`, which is transient.
Unset options fall back to `AlwaysOnlineConnectivityService`, `TtlStalenessEvaluator` and
`SyncPolicy.CacheFirst(1 day)`. The store has no default on this class; see Registration above.

| Option | Default |
|---|---|
| `DefaultPolicy` | `SyncPolicy.CacheFirst()` — TTL taken from `DefaultCacheTtl` |
| `Connectivity` | `AlwaysOnlineConnectivityService` |
| `StalenessEvaluator` | `null` — a `TtlStalenessEvaluator` is built at registration from the effective TTL |
| `DefaultCacheTtl` | 5 minutes |
| `OfflineResponsePolicy` | `Transparent` |
| `MaxCachedResponseBodyBytes` | 524,288 (512 KB) |
| `ConnectivityDebounceDelay` | 2 seconds |
| `FlushOnStartup` | `true` |
| `DefaultRetryOptions` | 5 retries, 2s initial delay, ×2 backoff |

`CabinetStoreOptions`, configured through `AddHyperwyc`'s second delegate, carries the
storage-specific settings — `DirectoryPath` (default `{LocalApplicationData}/Hyperwyc`) and
`EncryptionKey` (default: derived from the path). These deliberately live outside
`HyperwycOptions`, which stays free of concepts that apply to only one store.

**Effective TTL** resolves at registration, after the caller's configuration has run: a TTL
given to `SyncPolicy.CacheFirst(ttl)` wins, otherwise `DefaultCacheTtl` supplies it. The default
policy deliberately carries no TTL of its own, so setting `DefaultCacheTtl` alone is honoured
rather than being overwritten by a default nobody chose. The default staleness evaluator is
constructed only once that resolution is complete.

---

### 8. Retry and Resilience

- **Retry strategy:** Polly exponential backoff with jitter; configured via
  `ISyncPolicy.GetRetryOptions(request)`, which receives the request and can therefore vary
  per endpoint.
- **Dead-letter policy:** envelopes failing after the retry threshold are flagged
  `IsDeadLettered` and excluded from subsequent outbox queries; `OnFailed` is published.
- **Manual requeue:** not yet available — dead-lettered envelopes can currently only be
  cleared by `ResetStoreAsync()`. Tracked in
  [issue 24](Backlog/24-v1-dead-letter-management.md).

---

### 9. Security and Privacy

- **Encryption at rest** is provided by `CabinetSyncStore` (AES-256-GCM). When no key is
  supplied, one is derived from the store path via SHA-256. That default requires no
  configuration and keeps cached data from casual inspection of the device filesystem, but it
  is deterministic for a given path and so is not a defence against an attacker holding the
  device who knows what this library does. Callers whose cached data warrants more should set
  `CabinetStoreOptions.EncryptionKey`, ideally from platform secure storage. Losing that key
  means losing access to everything already stored.
- **Multi-user / cache isolation:** the store is not scoped to a user identity. On logout or
  account switch, applications should call `IHyperwyc.ResetStoreAsync()` to clear all cached
  data. A user-scoped store with automatic partitioning is a v2.0 candidate.

> **Not yet implemented:** persisted envelopes currently retain **all** request headers,
> including `Authorization` and `Cookie`, and the orchestrator replays them verbatim through
> a transport that has no auth handler in it. Header exclusion, the opt-in full-persistence
> flag, and the question of how replays acquire fresh credentials are tracked in
> [issue 30](Backlog/30-sensitive-header-exclusion.md).

---

## Prior Art & Positioning

### CommunityToolkit.Datasync

- **Philosophy:** Entity-level synchronisation between client and server tables.
- **Server coupling:** Requires an ASP.NET Core backend with Datasync server components.
- **Domain model:** Mirrors database entities to the client; assumes close schema alignment
  between client and API.
- **Offline model:** Synchronises entire table sets; the API surface must match the data model.
- **Drawback:** Requires rearchitecting around the sync engine; applications must shape their
  domain model to fit Datasync's expectations.

### Realm

- **Philosophy:** Persistent object graph synchronised with a MongoDB Atlas backend.
- **Server coupling:** Requires MongoDB Realm backend services (now EOLed in favour of Atlas SDKs).
- **Domain model:** Heavily coupled to the Realm storage format and object model.
- **Drawback:** Tight backend lock-in and schema mirroring; unsuitable for REST- or
  GraphQL-based APIs.

### Hyperwyc (Differentiation)

- **Philosophy:** Service-worker-inspired HTTP handler — transparent request/response caching
  and replay at the transport layer. The caller receives normal-looking responses regardless
  of connectivity state.
- **Server coupling:** None — works with any HTTP backend.
- **Domain model:** Fully independent; no schema mirroring, no requirement to align API
  surface with storage.
- **Integration:** Drop-in `DelegatingHandler`; can be added to any existing app without
  restructuring.
- **Use case fit:** Ideal for apps where API contracts are already stable, or where data
  conflicts are rare or handled server-side.

In essence, Datasync and Realm require you to architect your app *around* their sync model.
Hyperwyc fits *into* your existing architecture — like adding a Service Worker to a web app:
invisible by default, powerful when you need it.

---

## Summary

**Hyperwyc** is a service-worker-inspired HTTP handler for .NET. It provides transparent
caching, offline queuing, and controlled retry — all without imposing data models or
framework dependencies. Like a Service Worker in a PWA, it's invisible to callers by default:
requests go out, responses come back, and the app never needs to know whether the network was
involved. Its composable handler-based design guarantees minimal intrusion into existing app
architecture, and its Cabinet storage foundation provides fast, dependency-free persistence
with a flexible indexing model tailored to the HTTP envelope pattern.
