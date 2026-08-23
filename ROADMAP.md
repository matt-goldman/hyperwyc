# Hyperwyc — Roadmap

Where Hyperwyc is going. For what it does *today*, see [TECHNICAL_PLAN.md](TECHNICAL_PLAN.md);
for per-item status, priority and dependencies, see the [backlog index](Backlog/README.md).

Checkboxes below reflect the state of the code, verified against `src/`. Each line links
to its backlog item where one exists.

---

## 🥇 v0.1 — MVP

> **Goal:** Working offline queue and response cache via the HTTP pipeline

**Shipped**

- [x] `HyperwycHandler` — core `DelegatingHandler`, online and offline paths ([06](Backlog/Done/06-hyperwyc-handler-online-path.md), [07](Backlog/Done/07-hyperwyc-handler-offline-path.md))
- [x] `Hyperwyc.Core` package: all interfaces, `IHyperwyc`, `InMemorySyncStore` ([02](Backlog/Done/02-core-interfaces.md), [04](Backlog/Done/04-in-memory-sync-store.md))
- [x] `CabinetSyncStore`, shipped in the `Hyperwyc` package as the default store ([14](Backlog/Done/14-cabinet-sync-store.md))
- [x] Request/response envelope model ([03](Backlog/Done/03-envelope-model.md))
- [x] Response cache with TTL staleness evaluation ([09](Backlog/Done/09-response-cache-read-operations.md))
- [x] Write-triggered GET cache invalidation on write success ([10](Backlog/Done/10-write-triggered-cache-invalidation.md))
- [x] Response body cache cap, 512 KB default ([17](Backlog/Done/17-max-cached-body-size.md))
- [x] Replay semaphore (single concurrent flush) and connectivity event debounce ([11](Backlog/Done/11-sync-flush-orchestrator.md))
- [x] Retry and dead-lettering ([12](Backlog/Done/12-polly-retry-dead-letter.md))
- [x] Connectivity-driven retry model, with persisted retry state ([38](Backlog/Done/38-retry-classification.md), [28](Backlog/Done/28-persisted-retry-state.md)) — one attempt per flush, `4xx` dead-lettered at once, `Polly` no longer needed
- [x] Reactive sync event stream (`IObservable<SyncEvent>`) ([05](Backlog/Done/05-sync-event-stream.md))
- [x] `AddHyperwyc()` DI extension for configuration ([15](Backlog/Done/15-di-extension-and-options.md))
- [x] Cache TTL resolved from policy or options, with a defined precedence ([29](Backlog/Done/29-default-ttl-propagation.md))
- [x] Configurable policies applied — cache-first, API-first, cache-only and network-only ([27](Backlog/Done/27-cache-strategy-not-applied.md))
- [x] Orchestrator disposal, synchronous and asynchronous, cancelling in-flight work ([33](Backlog/Done/33-orchestrator-sync-disposal.md))
- [x] `IHyperwyc.FlushAsync()` for manual sync; `SyncOrchestrator` internal ([36](Backlog/Done/36-public-surface.md))
- [x] Injectable replay transport ([35](Backlog/Done/35-orchestrator-transport-not-injectable.md))
- [x] Replays sent through the originating client's pipeline, so auth applies to them ([37](Backlog/Done/37-replay-through-pipeline.md))
- [x] Flush trigger model documented — no app lifecycle wiring required ([34](Backlog/Done/34-app-lifecycle-integration.md))
- [x] Sample product/sales API ([18](Backlog/Done/18-poc-web-api.md))
- [x] No headers added to outbound requests — idempotency left to the application and its API ([39](Backlog/Done/39-reconsider-idempotency.md), superseding [08](Backlog/Done/08-idempotency-key-injection.md))
- [x] Synthetic responses carry the `null` literal with no asserted media type, so `GetFromJsonAsync<T>` returns `null` rather than throwing ([26](Backlog/26-v2-typed-response-shaping.md), layer 0)
- [x] Package structure: `Hyperwyc` (batteries included) over `Hyperwyc.Core` ([31](Backlog/Done/31-package-structure.md)) — `AddHyperwyc()` gives a durable, encrypted store with no decision to make
- [x] Connectivity is required, and a BCL implementation ships ([47](Backlog/Done/47-connectivity-is-required.md)) — `NetworkAvailabilityConnectivityService` for non-MAUI consumers; register an `IConnectivityService` in the container (either side of `AddHyperwyc`) or set the option, rather than silently assuming always-online

**Remaining**

- [x] Surface the outcome of a deferred request ([40](Backlog/Done/40-surface-deferred-outcomes.md)) — events carry a correlation id and a persisted `SyncOutcome`, so a queued write's eventual rejection can be matched to the record that produced it and acted on
- [x] Connectivity documentation ([13](Backlog/Done/13-connectivity-reference-implementation.md)) — README carries the MAUI implementation, the decisions inside it, and how to fake connectivity in your own tests. Nothing ships from core, including test doubles
- [ ] `IHyperwyc.ResetStoreAsync()` — flush coordination and test coverage ([16](Backlog/16-reset-store-async.md)); the method itself exists
- [ ] Sample .NET MAUI app ([19](Backlog/19-poc-maui-app.md)) — offline reads proven on device across an app restart; offline writes and sync UI remain. See [POC.md](POC.md)

---

## 🥈 v1.0 — release candidate

> **Goal:** the two gaps that genuinely prevent calling this a release candidate

Everything else on the backlog can be worked around by a consumer. These two cannot.

- [ ] Binary request and response bodies ([25](Backlog/25-binary-request-response-bodies.md)) — bodies round-trip through `ReadAsStringAsync`, so file uploads, image downloads and protobuf are silently corrupted. Also a breaking change to the persisted shape, so it must land before there are users
- [ ] Fine-grained per-route policies ([22](Backlog/22-v1-per-route-policies.md)) — a single global policy cannot express "cache the catalogue for a day, never cache payments", which the README already promises

---

## 🥉 v1.2 — ergonomics and operational visibility

- [ ] Decide and implement what happens when the store cannot be decrypted ([49](Backlog/49-unreadable-store-recovery.md)) — today a key mismatch throws a raw `CryptographicException`, sometimes out of the consumer's own HTTP call
- [ ] Document excluding the store from iCloud and Google Drive backups ([48](Backlog/48-exclude-store-from-os-backup.md)) — a restored outbox replays writes that already happened, and Hyperwyc has no duplicate suppression by design
- [ ] MAUI `SecureStorage` reference implementation for the store encryption key ([32](Backlog/32-default-encryption-key.md)) — the path-derived key stays as the free default, and is documented as such
- [ ] `Date` header rewriting when serving responses from cache ([21](Backlog/21-v1-date-header-rewriting.md))
- [ ] In-app diagnostics view — list unsynced and dead-lettered records ([23](Backlog/23-v1-diagnostics-view.md))
- [ ] Dead-letter queue management — view, requeue, dismiss ([24](Backlog/24-v1-dead-letter-management.md))
- [ ] `MaxCachedResponseBodyBytes` validation and public documentation ([20](Backlog/20-v1-configurable-body-cache-cap.md))
- [ ] Document that caller-set headers are persisted ([30](Backlog/30-sensitive-header-exclusion.md)) — reversed from a deny-list, which would break replay for credentials that are still valid

---

## 🏅 v1.5 — HTTP caching semantics

From an audit against Service Worker and Workbox — see
[reference models](docs/decisions/README.md#reference-models). Valuable, and none of it blocks a
release candidate.

- [ ] Honour the server's cacheability directives ([41](Backlog/41-honour-cacheability-directives.md)) — `no-store` is currently ignored and the response written to disk
- [ ] Bound the cache and evict ([42](Backlog/42-cache-eviction.md)) — nothing currently limits total entries or size
- [ ] Honour `Vary` ([43](Backlog/43-honour-vary-header.md)) — the cache key is the URL alone, so content-negotiated endpoints serve the wrong variant
- [ ] Cache generation, so an app upgrade discards incompatible cached bodies ([44](Backlog/44-cache-generation.md))
- [ ] `StaleWhileRevalidate` strategy ([45](Backlog/45-stale-while-revalidate.md)) — instant from cache, refreshed in the background
- [ ] Conditional revalidation with `ETag` / `Last-Modified` ([46](Backlog/46-conditional-requests.md)) — a `304` instead of a full re-download

---

## 🎖 v2.0+

> **Goal:** Broader platform support and advanced scenarios

No backlog items written yet — these are direction, not commitments.

- [ ] `Hyperwyc.IndexedDb` — Blazor WASM store provider
- [ ] Additional store providers (`Hyperwyc.LiteDb`, `Hyperwyc.Sqlite`)
- [ ] User-scoped store — identity-partitioned cache isolation
- [ ] Background sync scheduler — time-based and app-start replay triggers
- [ ] Prefetch on boot or connectivity restoration — pre-warm cache for key routes
- [ ] Smart paging support — cache-aware handling of paginated responses
- [ ] Request grouping and bulk sync — batch multiple queued writes into a single operation
- [ ] GraphQL support — read-intent POST disambiguation
- [ ] **Under consideration:** Typed-response shaping for offline reads ([26](Backlog/26-v2-typed-response-shaping.md)) — revised down to three layers, the first of which is simply returning `null` rather than an empty body, since an empty body throws for objects as well as collections

---

## Out of Scope

Explored in earlier planning and intentionally excluded from Hyperwyc's scope:

| Item | Reason |
|------|--------|
| Conflict resolution (`IConflictResolver`) | Hyperwyc is designed for low-conflict scenarios; resolution is the responsibility of the backend or the consuming application |
| Entity/table synchronisation | Hyperwyc operates at the transport layer, not the data model layer |
| Auth / token management | Auth is the responsibility of a separate `DelegatingHandler` in the pipeline |
| Streaming request/response bodies | Buffered byte arrays only; chunked and indeterminate-length payloads are a follow-up if demand emerges (see [25](Backlog/25-binary-request-response-bodies.md)) |
| Runtime type inference for offline response shaping | Reflection over caller types is brittle, AOT-hostile and a layering violation (see [26](Backlog/26-v2-typed-response-shaping.md)) |
