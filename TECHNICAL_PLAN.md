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

| Package | Contents |
|---------|----------|
| `Hyperwyc` | Core: `HyperwycHandler`, all interfaces (`ISyncStore`, `IConnectivityService`, `ISyncPolicy`, `IStalenessEvaluator`), `SyncEventStream`, `SyncOrchestrator`, `IHyperwyc`, `InMemorySyncStore`, `AlwaysOnlineConnectivityService`, `TtlStalenessEvaluator`. Depends on `Microsoft.Extensions.DependencyInjection.Abstractions`, `Microsoft.Extensions.Hosting.Abstractions` and `Polly`; no storage dependency. |
| `Hyperwyc.Cabinet` | `CabinetSyncStore` — `ISyncStore` implementation backed by [Cabinet](https://github.com/mattgoldman/cabinet). Depends on `Hyperwyc`; installing this package delivers the core transitively. |

Future provider packages follow the same pattern: `Hyperwyc.LiteDb`, `Hyperwyc.IndexedDb`,
etc. Developers who want to implement their own store install only `Hyperwyc`.

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

1. For `GET`, `HEAD` and `OPTIONS` requests, when **online**:
   - Returns the cached response if one exists and `IStalenessEvaluator` considers it fresh.
   - Otherwise sends the request, and on a 2xx caches the response and publishes `OnUpdated`,
     subject to the body size cap.
2. When **offline**:
   - Returns the cached response if one exists, **even if stale** — any data beats no data.
   - Otherwise returns a synthetic `200 OK` with an empty body (`Transparent`) or `503`
     (`Signal`), with `X-Hyperwyc-Status: Offline`.
3. Cached responses are reconstructed with their original status code and headers.

> **Not yet applied:** `ISyncPolicy.GetStrategy` returns a `CacheStrategy`, but the handler
> does not consult it — all reads use cache-first semantics, so the `ApiFirst`, `CacheOnly`
> and `NetworkOnly` presets currently have no effect. Tracked in
> [issue 27](Backlog/27-cache-strategy-not-applied.md).

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

Hyperwyc's durable `ISyncStore` implementation uses **Cabinet**, whose document-oriented
model and flexible index system suit HTTP request/response envelopes.

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

services.AddHyperwyc(options =>
{
    options.DefaultPolicy = SyncPolicy.CacheFirst(TimeSpan.FromDays(1));
    options.Store = new CabinetSyncStore("Hyperwyc.db");
});
```

`AddHyperwyc` builds a `HyperwycOptions` instance, applies the caller's delegate, and
registers the resolved services as singletons — except `HyperwycHandler`, which is transient.
Unset options fall back to `InMemorySyncStore`, `AlwaysOnlineConnectivityService`,
`TtlStalenessEvaluator` and `SyncPolicy.CacheFirst(1 day)`.

| Option | Default |
|---|---|
| `DefaultPolicy` | `SyncPolicy.CacheFirst(TimeSpan.FromDays(1))` |
| `Store` | `InMemorySyncStore` |
| `Connectivity` | `AlwaysOnlineConnectivityService` |
| `StalenessEvaluator` | `TtlStalenessEvaluator` (5 minutes) |
| `DefaultCacheTtl` | 5 minutes |
| `OfflineResponsePolicy` | `Transparent` |
| `MaxCachedResponseBodyBytes` | 524,288 (512 KB) |
| `ConnectivityDebounceDelay` | 2 seconds |
| `FlushOnStartup` | `true` |
| `DefaultRetryOptions` | 5 retries, 2s initial delay, ×2 backoff |

> **Known gap:** a TTL passed to `SyncPolicy.CacheFirst` does not currently reach the default
> staleness evaluator, which is constructed with the 5-minute default before the policy's TTL
> is read. The snippet above therefore behaves as a 5-minute cache. Tracked in
> [issue 29](Backlog/29-default-ttl-propagation.md).

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

- **Encryption at rest** is provided by `CabinetSyncStore` (AES-256-GCM). A key derived from
  the store path is used when no explicit key is supplied; production callers should pass
  their own via the two-parameter constructor.
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
