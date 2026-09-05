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
no cached data return `200 OK` with a `null` body, and cached reads are served with their
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
| `Hyperwyc.Core` | `Hyperwyc.Core.dll` | `HyperwycHandler`, the interfaces (`IHyperwycStore`, `IConnectivityService`, `IHyperwyc`), `RoutePolicy`, `RoutePolicyMap`, `HyperwycEventStream`, `InMemoryStore`, `AlwaysOnlineConnectivityService`, `NetworkAvailabilityConnectivityService`. Depends on `Microsoft.Extensions.DependencyInjection.Abstractions`, `Microsoft.Extensions.Hosting.Abstractions` and `Microsoft.Extensions.Http`; no storage dependency. |
| `Hyperwyc` | `Hyperwyc.dll` | `CabinetStore` backed by [Cabinet](https://github.com/mattgoldman/cabinet), `CabinetStoreOptions`, and the batteries-included `AddHyperwyc()`. Depends on `Hyperwyc.Core` and `Cabinet`. |

`Hyperwyc` is the package almost everyone installs: `AddHyperwyc()` with no arguments produces
a working, durable configuration. `Hyperwyc.Core` is for developers supplying their own
`IHyperwycStore`, and future provider packages — `Hyperwyc.LiteDb`, `Hyperwyc.IndexedDb` — sit
alongside it.

Package ID, assembly name and root namespace are deliberately independent. `Hyperwyc.Core`
ships types in the `Hyperwyc` namespace, and `Hyperwyc` ships `CabinetStore` in
`Hyperwyc.Cabinet`, so a single `using Hyperwyc;` reaches the common surface regardless of
which packages are installed.

### Registration

| Entry point | Package | Store |
|---|---|---|
| `AddHyperwyc(configure?, configureStore?)` | `Hyperwyc` | `CabinetStore`, constructed by the container |
| `AddHyperwycCore<TStore>(configure?)` | `Hyperwyc.Core` | `TStore`, constructed by the container |
| `AddHyperwycCore(storeFactory, configure?)` | `Hyperwyc.Core` | Whatever the factory returns |

`IHyperwyc` is the whole consumer-facing runtime surface — `Events`, `FlushAsync` and
`ResetStoreAsync`. `OutboxProcessor` is internal: flushing is reached through the interface
rather than by depending on the concrete type. Configuration enums (`OfflineResponsePolicy`,
`SourcePriority`) live in the root `Hyperwyc` namespace rather than `Hyperwyc.Models`, so
configuring options needs no second `using`; `Hyperwyc.Models` holds only genuine data types
(`Envelope`, `CachedResponse`, `HyperwycEvent`, `RetryOptions`).

The store is a type parameter rather than a property on `HyperwycOptions`. That makes omitting
it a compile-time error, where an options property would allow an application to silently run
on `InMemoryStore` and lose every queued write at restart — the exact failure the library
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
- **Failure observation.** The first handler added is the last to see the response, so a handler
  registered after `HyperwycHandler` observes each response *before* Hyperwyc does. Combined with
  the replay retry wrapping the entire client (§3), this makes Hyperwyc's retry the last-resort
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
     the route policy's `InvalidateCacheOnWrite`), and publishes `OnDelivered`.

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

Read handling for `GET`, `HEAD` and `OPTIONS` is governed by the `SourcePriority` that
the route policy resolved for the request specifies:

| Strategy | Online | Offline |
|---|---|---|
| `CacheFirst` (default) | Serves a cached response within its TTL; otherwise fetches, caches and returns | Serves a cached response within its TTL; otherwise a synthetic offline response |
| `NetworkFirst` | Always fetches; falls back to a cached response within its TTL if the request throws | Serves a cached response within its TTL; otherwise a synthetic offline response |
| `NetworkOnly` | Always sends; never reads or writes the cache | Synthetic offline response; the cache is not consulted |

Successful responses are cached subject to the body size cap, and publish `OnUpdated`. Cached
responses are reconstructed with their original status code and headers.

`NetworkFirst`'s fallback triggers on `HttpRequestException` — the case where
`IConnectivityService` reports online but the API is not actually reachable (captive portal,
DNS failure, transient outage). `CacheFirst` deliberately does *not* fall back this way: it has
already considered the cache and judged it stale.

Synthetic read responses carry `X-Hyperwyc-Status: Offline`.

**TTL is a validity bound.** It states how old a stored response may be and still be served, and
means the same thing whether or not there is a network: online, an expired response is refetched;
offline, the caller gets the synthetic offline response as though nothing were cached.
`NetworkFirst`'s failure fallback honours it too.

It was briefly a refetch trigger — consulted online, ignored offline, so a short TTL silently
became "serve anything, however old" the moment connectivity dropped. That made one value mean
two things with a hidden mode switch, and left no way to say "this must not be served stale". One
meaning, and "fetch fresh whenever possible" is `NetworkFirst`, which is a strategy rather than a
TTL. The default rose from five minutes to one day at the same time: as a validity bound, five
minutes would make a cache useless for the offline sessions this library exists to serve.

`CacheOnly` was removed on 2026-09-05. It returned early before `CacheResponseIfEligibleAsync`,
so a route configured with it could never populate its own cache and missed on every call
forever. Workbox's equivalent works because precaching fills the store at install time; that is
prefetch-on-boot, still unbuilt. The consumer half of a two-part mechanism shipped without the
other half. `X-Hyperwyc-Status: CacheMiss` and `HyperwycResponseFactory.CacheMiss()` went with
it, having served only that strategy. Both return when precaching does.

**Every synthetic response carries the JSON `null` literal as its body**, including the `202`
for a queued write. An empty body is not JSON, so `GetFromJsonAsync<T>` throws on it — for a
single object as much as for a collection. `null` deserialises cleanly to a case application
code already has to handle. A collection still arrives as `null` rather than `[]`; emitting `[]`
needs knowledge Hyperwyc does not have; [issue 26](Backlog/Done/26-v2-typed-response-shaping.md) closed unbuilt.

#### Cache Invalidation Prefix

`DeriveInvalidationPrefix` strips a trailing path segment when it parses as a `long` or a
`Guid`, so a write to `/api/notes/42` invalidates the cached `/api/notes` collection as well
as the individual resource. Query strings are excluded from the prefix.

---

### 3. Delivery Workflow

`OutboxProcessor` owns outbound replay. It subscribes to
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
- On successful delivery: marks `IsSynced = true`, publishes `OnDelivered`, and applies
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

`IHyperwycStore.GetReadyToSendAsync(now)` returns envelopes never attempted plus those whose
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

`OutboxProcessor` implements both `IDisposable` and `IAsyncDisposable`, and both mean *stop
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
| `OutboxProcessor` | Drains the outbox on connectivity restoration or manual flush; owns retry and dead-lettering. Internal — reached through `IHyperwyc` |
| `HyperwycHostedService` | Triggers the startup flush |
| `HyperwycService` | Default `IHyperwyc` — exposes `Events`, `FlushAsync` and `ResetStoreAsync` |
| `IHyperwycStore` | CRUD over stored envelopes; pluggable |
| `InMemoryStore` | Non-durable store, for tests and for consumers who genuinely want no persistence. Never selected implicitly |
| `CabinetStore` | Durable `IHyperwycStore` using Cabinet |
| `IConnectivityService` | Reports online/offline state and raises change events |
| `AlwaysOnlineConnectivityService` | Always reports connected. Opt-in only; nothing is queued or replayed under it |
| `NetworkAvailabilityConnectivityService` | BCL-backed `IConnectivityService` over `NetworkInterface.GetIsNetworkAvailable()`. Opt-in; detects hard-offline, not an unreachable API |
| `RoutePolicy` | Cache strategy, TTL and write-invalidation for one route |
| `RoutePolicyMap` | Pattern-to-policy map; later registrations refine earlier ones |
| `HyperwycEventStream` | Reactive event publisher (`IObservable<HyperwycEvent>`) |
| `HyperwycResponseFactory` | Builds the synthetic `Queued` and `Offline` responses |

---

### 5. Storage — Cabinet

Hyperwyc's default `IHyperwycStore` implementation uses **Cabinet**, whose document-oriented
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

#### Route Policies

`RoutePolicyMap` maps URL patterns to `RoutePolicy` values. Both the handler and
`OutboxProcessor` resolve through the same map, so a replayed write honours its route's
invalidation decision rather than a global one.

**Later registrations refine earlier ones**, so a map is written general to specific. That is
`.gitignore`'s model and the CSS cascade's, and it matches how the configuration is composed:
state the general rule, then carve out exceptions.

Resolution by computed specificity was rejected. With this pattern language — exact paths, or a
prefix ending `/*` — any two patterns that both match a path necessarily nest, so "more specific"
and "registered later" always agree. Computing specificity would be an invisible rule producing
the same answer as a visible one.

Matching is on `AbsolutePath`, case-insensitive, ignoring scheme, host, port and query string, so
a pattern is independent of the client's `BaseAddress`. `/api/sales/*` matches `/api/sales` as
well as everything beneath it.

`RoutePolicy` carries `Strategy`, `Ttl` and `InvalidateCacheOnWrite`. It is a value, not a
function of a request: deciding per request is not a policy but a handler, which is why this
replaced `ISyncPolicy`, whose two members both took an `HttpRequestMessage` that no shipped
implementation ever read.

`SourcePriority.NetworkOnly` is the only strategy that governs writes as well as reads — an
offline write to such a route is not queued, and passes through to the transport to fail as it
would without Hyperwyc installed.

#### Cache Entry Identity

A cache envelope's `Id` is derived from the URL (`cache:{url}`), not generated. `IHyperwycStore.UpsertAsync`
keys on `Id`, so a refetch **replaces** the stored response rather than adding a second envelope
beside it.

This was a defect until 2026-08-27. Cache envelopes were created with a fresh `Guid` like any
other, so every refetch inserted, and `GetCachedResponseAsync`'s `FirstOrDefault` returned
whichever the store enumerated first — in practice the oldest. The cache froze at the first
response ever stored and never updated, however many times the application refetched, while the
store grew by an unreadable envelope per refetch. Found in the sample: a sale recorded online
updated the quantities on screen, then going offline showed the original ones again.

The prefix keeps cache entries from colliding with queued writes, whose ids remain random. The
key is the URL alone, matching what `GetCachedResponseAsync` looks up by; honouring `Vary`
([issue 43](Backlog/43-honour-vary-header.md)) or a cache generation
([issue 44](Backlog/44-cache-generation.md)) would extend it in that one place.

#### Body Handling and Size Limits

Request and response bodies are stored as **`byte[]`** — `Envelope.RequestBody` and
`CachedResponse.Body` — read through `ReadAsByteArrayAsync` and rehydrated as `ByteArrayContent`.
File uploads, image downloads, protobuf and gzip-encoded payloads round-trip byte for byte
([issue 25](Backlog/Done/25-binary-request-response-bodies.md)).

`ByteArrayContent` is deliberate on both paths: unlike `StringContent` it stamps no
`Content-Type` of its own, so the captured headers are the single source of truth for the media
type. A `StringContent` default was what made every replayed JSON write go out as `text/plain`.

For textual routes, `GetRequestBodyAsText()` on `Envelope`, `GetBodyAsText()` on
`CachedResponse` and `DeliveryOutcome`, and `GetRequestBodyAsText()` on `HyperwycEvent` decode as UTF-8.
They are conveniences for diagnostics and for callers who know their route is text; nothing
inside Hyperwyc uses them, because nothing inside Hyperwyc is entitled to assume a body is text.

**Streaming is out of scope.** Bodies are buffered in full before being stored, so an
indeterminate-length upload or a very large download is materialised in memory. That bound is
what `MaxCachedResponseBodyBytes` exists for on the read path; there is no equivalent cap on
queued writes.

`HyperwycOptions.MaxCachedResponseBodyBytes` (default 524,288 — 512 KB) caps response caching.
Responses exceeding the limit are returned to the caller but not stored. The default was
reconsidered when bodies became binary and deliberately left alone: binary payloads are larger,
but raising the cap invites unbounded store growth while cache eviction
([issue 42](Backlog/42-cache-eviction.md)) does not exist, and a consumer who wants to cache
large binaries can raise it themselves. Outbound requests are queued regardless of body size.

---

### 6. Reactive Event Stream

Hyperwyc exposes sync lifecycle changes as `IObservable<HyperwycEvent>`:

```csharp
IObservable<HyperwycEvent> Events { get; }
```

`IObservable<T>` is a BCL interface (`System.IObservable<T>`). The core package implements it
with a minimal hand-rolled subject — no dependency on `System.Reactive`. Consumers who want
Rx operators (`.Where()`, `.Select()`, `.Throttle()`) add `System.Reactive` themselves. This
keeps the core dependency graph clean.

Plain .NET events are not exposed. `IObservable<T>` is strictly more capable; consumers who
prefer event-style consumption get it with a one-line `.Subscribe(...)`.

| Event | Trigger | Payload beyond type/url/method/timestamp |
|-------|---------|---|
| `OnQueued` | Request cached to outbox (offline) | `CorrelationId`, `RequestId`, `RequestBody` |
| `OnDelivered` | Outbound request successfully delivered | The above, plus this attempt's `Outcome` |
| `OnFailed` | Request moved to dead-letter after threshold | The above, plus the final `Outcome` |
| `OnUpdated` | Cached response refreshed from API | None — not a queued write |

#### Correlation

Every outbox envelope carries a `CorrelationId`, taken from
`HyperwycRequestOptions.CorrelationId` when the caller set one and otherwise defaulted to
`Envelope.Id`. It is returned on the synthetic `202` as `X-Hyperwyc-Correlation-Id`, always,
whichever source it came from.

`HttpRequestOptions` is local to the message and never transmitted, so accepting per-request
instruction this way does not breach ADR 0001. It is kept distinct from `Envelope.Id` because it
is under the application's control and carries no uniqueness guarantee, whereas `Envelope.Id`
keys the store.

#### `DeliveryOutcome`

`DeliveryOutcome` is persisted on `Envelope.LastOutcome` and the same instance is handed out on the
event. The durable record is the primary artefact: a background flush can complete while the
application is not running, so an outcome delivered only as an event is one nobody hears about.
Consequently everything on it must serialise, which is why transport failures carry
`string? Error` rather than an `Exception`.

`Kind` describes what happened, and says on its own whether Hyperwyc is finished:
`Succeeded` and `Rejected` are final, `TransientFailure` and `TransportFailure` leave the
envelope in the outbox for the next flush. Response bodies are captured as `byte[]` up to
`MaxOutcomeBodyBytes` (16 KB default), clipped with `BodyTruncated` rather than dropped, read
from the stream so an oversized body is never fully buffered.

Only failures are persisted. A delivered envelope leaves the outbox, so retaining its outcome
would mean a "recently completed" table with its own growth and eviction problem; reconciliation
after a process death is therefore best-effort, and the documented answer is to re-read the
resource. A transport failure is recorded on the envelope but publishes no event, since the flush
abandons and the envelope keeps its place.

The outcome is written with a plain `UpsertAsync` before `MoveToDeadLetterAsync`, leaving
`IHyperwycStore` unchanged. The pair is not atomic: a crash between them leaves the envelope carrying
its outcome but still pending, so it is retried and — classification being deterministic on the
status code — reaches the same verdict.

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
Unset routes fall back to `Routes.Default`.
Neither the store nor connectivity has a default; see Registration above and Connectivity
below.

| Option | Default |
|---|---|
| `Routes` | An empty `RoutePolicyMap` whose `Default` is `RoutePolicy.CacheFirst()` — CacheFirst, one-day TTL, invalidate on write |
| `Connectivity` | **No default — required.** Usually supplied by registering an `IConnectivityService` rather than by setting this; resolving throws if neither is done |
| `MaxCachedResponseBodyBytes` | 524,288 (512 KB) |
| `ReplayTransport` | `null` — a plain `HttpClientHandler` is used |
| `FlushOnStartup` | `true` |

#### Connectivity has no default

`Connectivity` is the one required setting, and the only one most consumers satisfy through
the container rather than through options:

```csharp
services.AddSingleton<IConnectivityService, MyConnectivityService>();
```

This is a deliberate departure from the batteries-included posture applied everywhere else.
The distinction is what the library can legitimately decide on the consumer's behalf: any
durable store will do, so Hyperwyc picks one, but the correct connectivity source depends on
the platform, which only the application knows. Defaulting to `AlwaysOnlineConnectivityService`
fails silently — every request takes the network path, nothing is queued, nothing is replayed,
and the library appears to work.

**The check is deferred rather than made at registration.** When `Connectivity` is unset,
`AddCoreServices` registers a placeholder factory that throws an `InvalidOperationException`
naming all three implementations, instead of throwing on the spot. That makes the container
route order-independent in both directions: `TryAdd` stands aside for a registration that came
first, and a registration that comes later wins, because the last descriptor for a service is
the one the container resolves.

Order-independence matters more than failing at startup. A consumer should not have to know
where `AddHyperwyc` sits relative to their own registrations, and a source generator wiring up
an `IConnectivityService` has no say in where its output lands. The cost is that a consumer who
supplies nothing finds out on first use — resolving `IHyperwyc`, `HyperwycHandler` or the
startup flush — rather than at registration. The message is the same either way.

When both routes are used, the container registration wins, matching the precedence every other
service here follows.

The three supported implementations, in the order most consumers should consider them:

| Implementation | Ships in | Detects |
|---|---|---|
| One written against the platform (`Connectivity.Current` on MAUI, ~20 lines) | Copied from the sample | Whatever the platform reports, including `ConstrainedInternet` |
| `NetworkAvailabilityConnectivityService` | `Hyperwyc.Core` | An operational non-loopback interface. Not a captive portal, not an unreachable API |
| `AlwaysOnlineConnectivityService` | `Hyperwyc.Core` | Nothing. Reports connected always |

A false positive costs a wasted attempt, not correctness: the request fails at the transport
and `OutboxProcessor` abandons the flush and waits for the next signal (§3).

`CabinetStoreOptions`, configured through `AddHyperwyc`'s second delegate, carries the
storage-specific settings — `DirectoryPath` (default `{LocalApplicationData}/Hyperwyc`) and
`EncryptionKey` (default: derived from the path). These deliberately live outside
`HyperwycOptions`, which stays free of concepts that apply to only one store.

**There is no effective-TTL resolution.** A resolved `RoutePolicy` carries one concrete TTL, so
nothing is reconciled at registration. Two sources for one value, resolved in the wrong order, is
exactly what [issue 29](Backlog/Done/29-default-ttl-propagation.md) was; the ambiguity is deleted
rather than fixed.

---

### 8. Retry and Resilience

- **Retry strategy:** one attempt per flush. A transiently failed envelope is deferred to a
  scheduled later attempt with exponential backoff and jitter, rather than retried in place.
  Configured via the route policy, which
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

- **Encryption at rest** is provided by `CabinetStore` (AES-256-GCM). When no key is
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

**TODO:** Evaluate language used; we're using "sync" a lot in the code and it conflates Hyperwyc with these other solutions. May be fine, but needs an active decision rather than it just falling out of what we did.

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

---

## Removed in the ADR 0004 audit

Recorded because absence is invisible: someone reading this document should not have to work out
from silence that these were considered and taken out.

[ADR 0004](docs/decisions/0004-default-to-removal.md) audited the surface against ADR 0001's
scope test and removed everything the project had already decided against but was still carrying.

**All retry apparatus.** `RetryOptions`, `ISyncPolicy.GetRetryOptions`,
`HyperwycOptions.DefaultRetryOptions` and `ConnectivityDebounceDelay`, `Envelope.RetryCount` and
`NextRetryUtc`, `IHyperwycStore.GetReadyToSendAsync`, exponential backoff with jitter, the follow-up
scheduler, the connectivity debounce, `HyperwycEventType.OnRetrying`, and `DeliveryOutcome.AttemptCount`
and `IsFinal`.

[Issue 38](Backlog/Done/38-retry-classification.md) had already concluded that Hyperwyc retries
*connectivity* failures on *connectivity change*, and
[ADR 0002](docs/decisions/0002-replays-traverse-the-pipeline.md) established that replays
traverse the application's pipeline — so the app's own resilience handler already covers `5xx`
and `429`. The apparatus was serving a responsibility we had declined.

What replaces it: a `4xx` dead-letters on the first attempt; anything else leaves the envelope in
the outbox for the next flush, which happens on connectivity restored or application start. No
budget, no curve, no timers. An envelope can wait indefinitely against a permanently broken
endpoint, which is honest and better than destroying a write.

**`IStalenessEvaluator` and `TtlStalenessEvaluator`.** One implementation, one caller, and the
indirection caused [issue 29](Backlog/Done/29-default-ttl-propagation.md). Now a TTL comparison
in the handler. [Issue 41](Backlog/41-honour-cacheability-directives.md) is where per-response
staleness earns an interface back.

**`OfflineResponsePolicy`.** The `Signal` mode had no users and asserted the consumer should have
an opinion. Synthetic responses now have one shape: a normal-looking success, distinguished by
`X-Hyperwyc-Status` and — for writes — the `202`.

**`DeliveryOutcome.Headers`.** A full response-header dictionary captured on every outcome that
nothing read.

Nothing here is foreclosed. `Hyperwyc.Core` with a consumer-supplied `IHyperwycStore` is the seam
through which any of it can be re-added by whoever actually needs it, which is what made removing
it safe rather than reckless.

---

## Vocabulary

Settled 2026-09-05. Two rules, and the reasoning is worth keeping because naming shaped design
decisions here more than once.

**"Sync" is gone.** It named bidirectional synchronisation with conflict resolution — Realm,
CommunityToolkit Datasync, the category "What It Doesn't Do" exists to distance Hyperwyc from.
What the write path actually does is queue and deliver, and that vocabulary was already in the
code (`outbox`, `flush`). So: `IHyperwycStore`, `InMemoryStore`, `CabinetStore`,
`OutboxProcessor`, `HyperwycEvent`, `DeliveryOutcome`, `MarkDeliveredAsync`, `OnDelivered`.

**"Cache" stayed, and narrowed.** Every use of it is genuine caching, and `CacheFirst` /
`NetworkFirst` are Workbox's names — vocabulary developers already have. The problem was never
the word; it was that "cache" was the *only* word, so it stretched over the write path where it
does not fit. Once "outbox" and "deliver" cover writes, "cache" retracts to what it describes.

**`CacheStrategy` became `SourcePriority`.** It was the one casualty: `NetworkOnly` governs
writes as well as reads, so a name claiming "cache" over-reached. `NetworkStrategy` would have
been the same mistake mirrored — every candidate that named one side left a third of the values
arguing with it. `SourcePriority` names the *relationship*: which source takes precedence, with
`NetworkOnly` the degenerate case where one has all of it. `RoutePolicyMap.Resolve` became
`PolicyFor` in the same pass, so "resolving a policy" and "resolving a request" stopped sharing
a word.

**`Envelope.IsSynced` was deliberately left.** It is set `true` on cached responses, so it means
"not a pending outbox entry" — renaming it `IsDelivered` would have been actively false rather
than merely vague. That it could not be renamed is the tell that the model is wrong, not the
name; tracked as [issue 55](Backlog/55-envelope-kind-discriminator.md).

Run as a pure mechanical pass with no behaviour edits, test count identical either side (29 and
263). Backlog items in `Done/` keep the old vocabulary, since they record what was decided when.
