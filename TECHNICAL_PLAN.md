# Hyperwyc — Technical Plan

**What Hyperwyc does today, and why it is built that way.** This document describes the
shipped architecture. Anything not yet built lives in [ROADMAP.md](ROADMAP.md) and the
[backlog index](Backlog/README.md); where current behaviour falls short of the intended
design, this document says so and links to the item that closes the gap. Decisions about
Hyperwyc's *scope* — what it deliberately does not do, and why — are recorded as
[architecture decision records](docs/decisions/README.md).

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
   API design. This is not aspirational, it's a core principle: it is why Hyperwyc adds no headers
   to outbound requests, and the basis of the scope test in
   [ADR 0001](docs/decisions/0001-idempotency-is-not-hyperwycs-remit.md).
6. **Composability** — Integrates via `DelegatingHandler` and works alongside other handlers
   such as auth or logging.
7. **Predictable behaviour** — Requests sent when possible, responses returned when possible,
   following cache and retry policies.

---

## Package Structure

| Package | Assembly | Contents |
|---------|----------|----------|
| `Hyperwyc.Core` | `Hyperwyc.Core.dll` | `HyperwycHandler`, all interfaces (`ISyncStore`, `IConnectivityService`, `ISyncPolicy`, `IStalenessEvaluator`), `SyncEventStream`, `IHyperwyc`, `InMemorySyncStore`, `AlwaysOnlineConnectivityService`, `TtlStalenessEvaluator`. Depends on `Microsoft.Extensions.DependencyInjection.Abstractions`, `Microsoft.Extensions.Hosting.Abstractions` and `Microsoft.Extensions.Http`; no storage dependency. |
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

`IHyperwyc` is the whole consumer-facing runtime surface — `SyncEvents`, `FlushAsync` and
`ResetStoreAsync`. `SyncOrchestrator` is internal: flushing is reached through the interface
rather than by depending on the concrete type. Configuration enums (`OfflineResponsePolicy`,
`CacheStrategy`) live in the root `Hyperwyc` namespace rather than `Hyperwyc.Models`, so
configuring options needs no second `using`; `Hyperwyc.Models` holds only genuine data types
(`Envelope`, `CachedResponse`, `SyncEvent`, `RetryOptions`).

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
- **Usage:** Added via `AddHyperwycHandler()` on `IHttpClientBuilder`, which captures the
  client's name from `IHttpClientBuilder.Name`. That name is stamped on queued envelopes and is
  how a replay finds its way back to the right pipeline. `AddHttpMessageHandler<HyperwycHandler>()`
  still works but yields no client name, so replays fall back to `ReplayTransport`.
- **Processing order:** register `HyperwycHandler` **first**. Handlers added after it run on
  ordinary requests *and* on replays, which is what allows a replayed write to be authenticated
  with a token minted at replay time rather than at queue time.
- **Two exceptions to "handlers after it always run":**
  - On the **offline path**, synthetic responses short-circuit the pipeline, so nothing
    downstream is invoked — there is no outbound request to authenticate or stamp.
  - Because the handler queues a request *before* downstream handlers have run, a queued
    envelope reflects the request as Hyperwyc saw it. It will not contain an `Authorization`
    header added further down. This is why replays must traverse the pipeline rather than being
    replayed verbatim.
- **Failure observation.** Pipelines are first-in, last-out, so a handler registered after
  `HyperwycHandler` observes each response *before* Hyperwyc does. Combined with the replay
  retry wrapping the entire client (§3), this makes Hyperwyc's retry the outermost, last-resort
  one: it acts only on failures the application's own handlers — refresh-on-401, circuit
  breakers, custom retry — could not resolve. Hyperwyc therefore does not need configuring to
  avoid interfering with them. Since a flush makes only one attempt per envelope (§3), an
  application's own retry handler composes rather than compounds: it retries within that single
  attempt, and Hyperwyc decides only whether there should be a further attempt at all.

### 2. Request Handling Logic

The handler branches first on connectivity, then on whether the method is mutating
(`POST`, `PUT`, `PATCH`, `DELETE`) or not.

#### Outgoing Requests (Write Operations)

1. The handler checks connectivity via `IConnectivityService`.
2. If **offline**:
   - Buffers and serialises the request into an envelope.
   - Persists it with `IsSynced = false`.
   - Returns a synthetic `202 Accepted` (`OfflineResponsePolicy.Transparent`, the default)
     or `503 Service Unavailable` (`Signal`), always with `X-Hyperwyc-Status: Queued`.
   - Publishes `OnQueued`.
3. If **online**:
   - Sends the request immediately.
   - On success, invalidates cached GETs sharing the URL prefix (configurable via
     `ISyncPolicy.ShouldInvalidateCacheOnWrite`), and publishes `OnSynced`.

Online writes are not persisted to the outbox — they either succeed against the origin or
their failure status is returned to the caller unchanged. Only offline writes are queued.

**No headers are added to outbound requests.** Hyperwyc previously injected an
`Idempotency-Key` on every mutating request, which is only useful if the backend implements
it, and if the backend implements it, the client implements it. A transport-level library
should not make of an API it knows nothing about (issue 39). Duplicate delivery is a
property of retrying in general, not of Hyperwyc, and it is resolved between an application
and its API.

What Hyperwyc does guarantee is fidelity: request headers are captured into the envelope and
replayed byte-identically on every attempt. An application that sets its own idempotency or
correlation key at the call site — where "this is one logical operation" is actually known — gets
that key carried through unchanged, however many retries it takes. `Envelope.Id` is internal
bookkeeping and is never sent.

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
- Envelopes are replayed in stored order via `HyperwycOptions.ReplayTransport`, bypassing
  `HyperwycHandler` so replays are not re-queued. That bypass means handler-level concerns the
  application applies to ordinary requests — certificate pinning, proxies, timeouts, logging —
  do not reach replays unless that transport supplies them. It defaults to a plain
  `HttpClientHandler`, and is never disposed by Hyperwyc: a caller-supplied handler stays the
  caller's to dispose, and the default lives for the application's lifetime.
- On successful delivery: marks `IsSynced = true`, publishes `OnSynced`, and applies
  write-triggered invalidation.
- **One attempt per envelope per flush.** There is no in-flush retry loop; a failed attempt
  resolves to one of three outcomes below. This is what keeps a single undeliverable write from
  holding up everything queued behind it, given the outbox drains sequentially.
- On a **`4xx`**: dead-letter immediately and publish `OnFailed`. The status describes the
  request, so replaying it unchanged cannot produce a different answer. `408` and `429` are
  excluded — both explicitly mean "later".
- On a **`5xx`, `408` or `429`**: increment `Envelope.RetryCount`, set `Envelope.NextRetryUtc`
  to an exponentially backed-off time, and leave the envelope queued. Both fields are persisted,
  so a budget survives the process being killed mid-flush.
- On a **transport exception**: abandon the remainder of the flush. The premise — that the
  network is reachable — has been falsified, so the remaining envelopes would fail identically.
  Nothing is held against them: no retry count, no scheduled time, and they stay immediately
  eligible for the next attempt.
- When `RetryCount` exceeds the configured budget: dead-letter and publish `OnFailed`.

Retrying connectivity failures on connectivity change is the whole of Hyperwyc's remit here.
Failures an application knows how to resolve — refreshing a token, tripping a circuit breaker,
retrying a flaky endpoint — are handled by its own handlers, which run first (§1).

#### Which envelopes a flush attempts

`ISyncStore.GetReadyToSendAsync(now)` returns envelopes never attempted plus those whose
scheduled retry time has arrived — deliberately excluding ones still waiting, so a flush does
not re-attempt deferred work. `GetPendingOutboxAsync` reports everything queued regardless of
readiness, which is what diagnostics wants.

When a flush defers anything, the orchestrator schedules a single follow-up pass for when the
earliest of them becomes eligible. Without it, a transient server failure on a device that stays
online would wait for the next connectivity change or app start. This terminates because every
deferral increments `RetryCount`, so an envelope that keeps failing eventually dead-letters and
stops being rescheduled.

#### What triggers a flush

Only two things: application startup, when `FlushOnStartup` is set and the device is online;
and connectivity being restored while the app runs. Plus `IHyperwyc.FlushAsync`, for a manual
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
| `SyncOrchestrator` | Drains the outbox on connectivity restoration or manual flush; owns retry and dead-lettering. Internal — reached through `IHyperwyc` |
| `HyperwycHostedService` | Triggers the startup flush |
| `HyperwycService` | Default `IHyperwyc` — exposes `SyncEvents`, `FlushAsync` and `ResetStoreAsync` |
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
    .AddHyperwycHandler()
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
| `ReplayTransport` | `null` — a plain `HttpClientHandler` is used |
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

- **Retry strategy:** one attempt per flush. A transiently failed envelope is deferred to a
  scheduled later attempt with exponential backoff and jitter, rather than retried in place.
  Configured via `ISyncPolicy.GetRetryOptions(request)`, which receives the request and can
  therefore vary per endpoint. Backoff is clamped to one hour so a generous budget cannot
  schedule an attempt absurdly far out.
- **No resilience-library dependency.** Retry was previously a Polly pipeline inside each
  flush. Once attempts are spaced by connectivity events and scheduled times rather than by an
  in-process backoff loop, what remains is arithmetic, and `Polly` was dropped from
  `Hyperwyc.Core` — worth having in a core package aimed at mobile.
- **Dead-letter policy:** an envelope is flagged `IsDeadLettered` and `OnFailed` published when
  the server rejects it outright (`4xx`) or when `RetryCount` exceeds the budget. Dead-lettered
  envelopes are excluded from subsequent outbox queries.
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
