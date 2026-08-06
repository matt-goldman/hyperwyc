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
- [x] Idempotency-Key header injection on all mutating requests ([08](Backlog/Done/08-idempotency-key-injection.md))
- [x] Response cache with TTL staleness evaluation ([09](Backlog/Done/09-response-cache-read-operations.md))
- [x] Write-triggered GET cache invalidation on write success ([10](Backlog/Done/10-write-triggered-cache-invalidation.md))
- [x] Response body cache cap, 512 KB default ([17](Backlog/Done/17-max-cached-body-size.md))
- [x] Replay semaphore (single concurrent flush) and connectivity event debounce ([11](Backlog/Done/11-sync-flush-orchestrator.md))
- [x] Polly-based retry with exponential backoff and dead-lettering ([12](Backlog/Done/12-polly-retry-dead-letter.md))
- [x] Reactive sync event stream (`IObservable<SyncEvent>`) ([05](Backlog/Done/05-sync-event-stream.md))
- [x] `AddHyperwyc()` DI extension for configuration ([15](Backlog/Done/15-di-extension-and-options.md))
- [x] Cache TTL resolved from policy or options, with a defined precedence ([29](Backlog/Done/29-default-ttl-propagation.md))
- [x] Configurable policies applied — cache-first, API-first, cache-only and network-only ([27](Backlog/Done/27-cache-strategy-not-applied.md))
- [x] Orchestrator disposal, synchronous and asynchronous, cancelling in-flight work ([33](Backlog/Done/33-orchestrator-sync-disposal.md))
- [x] `IHyperwyc.FlushAsync()` for manual sync; `SyncOrchestrator` internal ([36](Backlog/Done/36-public-surface.md))
- [x] Injectable replay transport ([35](Backlog/Done/35-orchestrator-transport-not-injectable.md))
- [x] Replays sent through the originating client's pipeline, so auth applies to them ([37](Backlog/Done/37-replay-through-pipeline.md))
- [x] Flush trigger model documented — no app lifecycle wiring required ([34](Backlog/Done/34-app-lifecycle-integration.md))
- [x] Package structure: `Hyperwyc` (batteries included) over `Hyperwyc.Core` ([31](Backlog/Done/31-package-structure.md)) — `AddHyperwyc()` with no configuration gives a durable, encrypted store

**Remaining**

- [ ] `StaticConnectivityService` in core, MAUI connectivity as documented reference code ([13](Backlog/13-connectivity-reference-implementation.md)) — only `AlwaysOnlineConnectivityService` ships today
- [ ] `IHyperwyc.ResetStoreAsync()` — flush coordination and test coverage ([16](Backlog/16-reset-store-async.md)); the method itself exists
- [ ] Sample ASP.NET Core Web API ([18](Backlog/18-poc-web-api.md)) and .NET MAUI app ([19](Backlog/19-poc-maui-app.md)) — see [POC.md](POC.md)

---

## 🥈 v1.0

> **Goal:** Correctness, developer ergonomics, and operational visibility

- [ ] Binary request and response bodies ([25](Backlog/25-binary-request-response-bodies.md)) — lifts the current text-only limitation; breaking change to the persisted shape, so it lands first
- [ ] Sensitive-header exclusion from persisted envelopes ([30](Backlog/30-sensitive-header-exclusion.md)) — its credentials question was answered by [37](Backlog/Done/37-replay-through-pipeline.md)
- [ ] MAUI `SecureStorage` reference implementation for the store encryption key ([32](Backlog/32-default-encryption-key.md)) — the path-derived key stays as the free default, and is now documented as such
- [ ] Persisted retry state so the retry budget survives a restart or suspension ([28](Backlog/28-persisted-retry-state.md)) — a flush cut off by backgrounding currently discards its budget
- [ ] Fine-grained per-route policies — TTL, cache strategy, offline response policy, empty-offline body ([22](Backlog/22-v1-per-route-policies.md))
- [ ] `Date` header rewriting when serving responses from cache ([21](Backlog/21-v1-date-header-rewriting.md))
- [ ] In-app diagnostics view — list unsynced and dead-lettered records ([23](Backlog/23-v1-diagnostics-view.md))
- [ ] Dead-letter queue management — view, requeue, dismiss ([24](Backlog/24-v1-dead-letter-management.md))
- [ ] `MaxCachedResponseBodyBytes` validation and public documentation ([20](Backlog/20-v1-configurable-body-cache-cap.md))

---

## 🥉 v2.0+

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
- [ ] **Under consideration:** Typed-response shaping for offline reads ([26](Backlog/26-v2-typed-response-shaping.md)) — make `GetFromJsonAsync<T>` return a deserialisable default body offline without requiring an application-level response envelope

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
