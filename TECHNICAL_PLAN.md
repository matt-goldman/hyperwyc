# Restyc — Technical Plan

## Overview

Restyc is an offline-resilient HTTP request/response caching and replay engine. It intercepts API calls made via `HttpClient`, persists them to a local store, and ensures delivery or refresh once connectivity is available. Its core design prioritises agnosticism — it is not a data synchronisation framework, but a drop-in reliability layer that allows applications to behave consistently regardless of connectivity.

---

## Core Principles

1. **Transport-level durability** — Operates at the HTTP layer, not the data model layer.
2. **Backend agnostic** — Works with any REST API over HTTP/1.x. GraphQL (mutation-vs-query ambiguity on POST) and gRPC (binary framing, HTTP/2 semantics) are deferred to a future version.
3. **Storage pluggability** — The core package ships with interfaces only; store providers are separate, explicitly installed packages.
4. **No architectural imposition** — Developers do not need to alter their domain models or API design.
5. **Composability** — Integrates via `DelegatingHandler` and works seamlessly with other handlers like auth or logging.
6. **Predictable behaviour** — Requests sent when possible, responses returned when possible, following cache and retry policies.

---

## Package Structure

| Package | Contents |
|---------|----------|
| `Restyc` | Core: `RestycHandler`, all interfaces (`ISyncStore`, `IConnectivityService`, `ISyncPolicy`, `IStalenessEvaluator`), `SyncEventStream`, `IRestyc`, `InMemorySyncStore`. No storage dependency. |
| `Restyc.Cabinet` | `CabinetSyncStore` — `ISyncStore` implementation backed by [Cabinet](https://github.com/mattgoldman/cabinet). Depends on `Restyc`; installing this package delivers the core transitively. |

Future provider packages follow the same pattern: `Restyc.LiteDb`, `Restyc.IndexedDb`, etc. Developers who want to implement their own store install only `Restyc`.

---

## Technical Design

### 1. Pipeline Integration

- **Entry point:** `RestycHandler : DelegatingHandler`
- **Usage:** Added to the `HttpClient` pipeline via `IHttpClientFactory`.
- **Processing order:**
  - Place `RestycHandler` **before** authentication handlers for expiring tokens, so replayed requests pick up fresh tokens.
  - For non-expiring auth (e.g. API keys), order is flexible.

### 2. Request Handling Logic

#### Outgoing Requests (Write Operations)

1. For every mutating request (POST, PUT, PATCH, DELETE), an envelope is created with a unique `_id`. An `Idempotency-Key` header is injected using this `_id` on both the initial send and any subsequent replay, enabling server-side duplicate suppression.
2. The handler checks connectivity via `IConnectivityService`.
3. If **offline**:
   - Serialises the request into a local store document (envelope).
   - Marks as `IsSynced = false`.
   - Publishes `OnQueued` via the reactive stream.
4. If **online**:
   - Sends the request immediately.
   - On successful delivery, cached GET responses for the same URL prefix are invalidated by default (configurable via `ISyncPolicy`).
   - Optionally caches the response according to the configured staleness policy.
   - Marks the record as synced on success.

#### Incoming Responses (Read Operations)

1. For `GET`, `HEAD`, and `OPTIONS` requests:
   - Checks local cache first if the cache policy allows.
   - If cached and not stale: returns the cached response immediately. The `Date` header is rewritten to the current time; all other headers reflect the originally cached values.
   - If stale or missing: fetches from the API, updates the cache, publishes `OnUpdated`.
2. TTL-based cache invalidation is handled via `IStalenessEvaluator` (pluggable).
3. Write-triggered invalidation: when a mutating request succeeds, cached GET responses for the same URL prefix are invalidated. On by default; configurable via `ISyncPolicy`.

---

### 3. Synchronisation Workflow

#### Outbound Sync

- On connectivity restored, events are debounced (default: 2 seconds) before triggering a flush. A semaphore ensures only one sync flush runs at a time.
- Sends envelopes in chronological order (queued order preserved).
- On successful delivery: marks `IsSynced = true`, publishes `OnSynced`.
- On failure:
  - Failure is defined as a non-2xx response or timeout — not a connectivity-state change. A device that `IConnectivityService` considers online but cannot reach the API (captive portal, DNS failure, transient API outage) is handled by the retry budget, not by the offline queue.
  - Applies Polly-based retry policy (exponential backoff).
  - After max retries: moves to dead-letter collection, publishes `OnFailed`.

#### Inbound Cache Refresh

- Optionally refreshes cached items approaching expiration.
- Can trigger automatically on app start or connectivity restoration.

---

### 4. Core Components

| Component | Responsibility |
|-----------|----------------|
| `RestycHandler` | Intercepts and persists HTTP requests/responses; central pipeline entry point |
| `ISyncStore` | Defines CRUD operations for stored request envelopes; pluggable |
| `CabinetSyncStore` | Default `ISyncStore` implementation using Cabinet |
| `IConnectivityService` | Reports current online/offline state and raises change events |
| `ISyncPolicy` | Defines cache-first vs API-first rules and retry/backoff behaviour |
| `IStalenessEvaluator` | Determines whether a cached response is still valid |
| `SyncEventStream` | Reactive event publisher (`IObservable<SyncEvent>`) |

---

### 5. Storage — Cabinet

Restyc uses **Cabinet** as its default `ISyncStore` implementation. Cabinet's document-oriented model and flexible index system make it a natural fit for storing HTTP request/response envelopes.

#### Key Advantages

- **Custom index providers** — Restyc indexes envelopes by route, HTTP method, sync state, expiry, or any metadata field.
- **Route-based indexing** — The request URL (or normalised route pattern) is the primary index, enabling fast lookup of cached `GET` results and selective replay of outbound writes.
- **Fits the envelope model** — Each envelope is a standalone document; no schema rigidity.
- **Optimised for .NET** — Pure managed code, mobile-safe, no native library dependencies.

#### Cabinet Indexing Strategy for Restyc

```csharp
new EnvelopeIndex()
    .WithKey(e => e.Route)          // fast lookups for GET cache
    .WithKey(e => e.IsSynced)       // outbox queries
    .WithKey(e => e.NextRetryUtc)   // retry scheduler
    .WithKey(e => e.Method);        // filter by HTTP verb
```

This enables Restyc to efficiently answer:

- Get the cached response for `GET /api/notes`
- Get all unsynced outbound envelopes
- Get all envelopes due for retry
- Get all cached results for a given route prefix

#### Request/Response Envelope Schema

Each request/response pair is persisted as a single document:

```json
{
  "_id": "guid",
  "url": "/api/notes",
  "method": "POST",
  "headers": {},
  "body": "{...}",
  "isSynced": false,
  "createdUtc": "2025-10-11T10:00:00Z",
  "nextRetryUtc": null,
  "retryCount": 0,
  "response": {
    "status": 200,
    "body": "{...}",
    "cachedAt": "2025-10-11T10:00:10Z"
  }
}
```

#### Body Size Limits

Request and response bodies are stored as strings within the envelope. A configurable `MaxCachedResponseBodyBytes` option (default: 524,288 — 512 KB) limits response caching. Responses exceeding this limit are not cached but are still returned to the caller. Outbound requests are always queued regardless of body size.

---

### 6. Reactive Event Stream

Restyc exposes sync lifecycle changes as `IObservable<SyncEvent>`:

```csharp
IObservable<SyncEvent> SyncEvents { get; }
```

`IObservable<T>` is a BCL interface (`System.IObservable<T>`). The core package implements it internally with a minimal hand-rolled subject — no dependency on `System.Reactive` is required. Consumers who want Rx operators (`.Where()`, `.Select()`, `.Throttle()`, etc.) add `System.Reactive` themselves. This keeps the core dependency graph clean.

Plain .NET events are not exposed. `IObservable<T>` is strictly more capable; consumers who prefer event-style consumption can achieve it with a one-line `.Subscribe(...)` call.

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

### 8. Retry and Resilience

- **Retry strategy:** Polly-based exponential backoff; configurable default with per-endpoint overrides.
- **Dead-letter policy:** Requests failing after the retry threshold are moved to a `DeadLetter` collection and flagged with `OnFailed`.
- **Manual requeue:** Planned future enhancement — allow developer-initiated requeuing of dead-lettered payloads.

---

### 9. Security and Privacy

- Sensitive headers (e.g. `Authorization`) are **excluded** from persisted envelopes by default.
- Encryption-at-rest is configurable via Cabinet's storage options.
- Developers can opt into full request persistence (including headers) via a config flag.
- **Multi-user / cache isolation:** The store is not scoped to a user identity by default. On user logout or account switch, applications should call `IRestyc.ResetStoreAsync()` to clear all cached data. This method is part of the `IRestyc` interface. A user-scoped store with automatic partitioning is a future enhancement.

---

### 10. Future Enhancements

- `Restyc.IndexedDb` — Blazor WASM store provider
- Background sync scheduler
- Fine-grained per-endpoint TTL configuration
- In-app diagnostics view for unsynced and errored records
- User-scoped store (identity-partitioned cache isolation)

---

## Prior Art & Positioning

### CommunityToolkit.Datasync

- **Philosophy:** Entity-level synchronisation between client and server tables.
- **Server coupling:** Requires an ASP.NET Core backend with Datasync server components.
- **Domain model:** Mirrors database entities to the client; assumes close schema alignment between client and API.
- **Offline model:** Synchronises entire table sets; the API surface must match the data model.
- **Drawback:** Requires rearchitecting around the sync engine; applications must shape their domain model to fit Datasync's expectations.

### Realm

- **Philosophy:** Persistent object graph synchronised with a MongoDB Atlas backend.
- **Server coupling:** Requires MongoDB Realm backend services (now EOLed in favour of Atlas SDKs).
- **Domain model:** Heavily coupled to the Realm storage format and object model.
- **Drawback:** Tight backend lock-in and schema mirroring; unsuitable for REST- or GraphQL-based APIs.

### Restyc (Differentiation)

- **Philosophy:** Transport-level durability — request/response caching and replay, not table/entity synchronisation.
- **Server coupling:** None — works with any HTTP backend (REST, GraphQL, gRPC, streaming APIs).
- **Domain model:** Fully independent; no schema mirroring, no requirement to align API surface with storage.
- **Integration:** Drop-in `DelegatingHandler`; can be added to any existing app without restructuring.
- **Use case fit:** Ideal for apps where API contracts are already stable, or where data conflicts are rare or handled server-side.

In essence, Datasync and Realm require you to architect your app *around* their sync model. Restyc fits *into* your existing architecture — it's additive, not invasive.

---

## Summary

**Restyc** is a lightweight, backend-agnostic HTTP caching and replay system for .NET. It ensures reliable network behaviour, offline operation, and controlled retry mechanisms — all without imposing data models or framework dependencies. Its composable handler-based design guarantees minimal intrusion into existing app architecture, and its Cabinet storage foundation provides fast, dependency-free persistence with a flexible indexing model tailored to the HTTP envelope pattern.
