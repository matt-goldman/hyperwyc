# hyperwyc — Roadmap

---

## 🥇 MVP (v0.1)

> **Goal:** Working offline queue and response cache via the HTTP pipeline

- [ ] `hyperwycHandler` — core `DelegatingHandler`
- [ ] `hyperwyc` core package: `hyperwycHandler`, all interfaces (`ISyncStore`, `IConnectivityService`, `ISyncPolicy`, `IStalenessEvaluator`), `Ihyperwyc`, `InMemorySyncStore`
- [ ] `hyperwyc.Cabinet` provider package: `CabinetSyncStore`
- [ ] `IConnectivityService` with default implementation using MAUI Essentials
- [ ] Configurable policies: cache-first / API-first, expiry TTL
- [ ] Idempotency-Key header injection on all mutating requests
- [ ] Write-triggered GET cache invalidation on write success (same URL prefix; configurable via `ISyncPolicy`)
- [ ] Default response body cache cap enforced (512 KB hardcoded; configurable in v1.0)
- [ ] Replay semaphore (single concurrent flush) and connectivity event debounce
- [ ] `Ihyperwyc.ResetStoreAsync()` for user logout / cache clearing
- [ ] Polly-based retry with exponential backoff; default + per-endpoint override
- [ ] Reactive sync event stream (`IObservable<SyncEvent>`)
- [ ] `Addhyperwyc()` DI extension for configuration
- [ ] Sample .NET MAUI app (see [POC.md](POC.md))

---

## 🥈 v1.0

> **Goal:** Developer ergonomics and operational visibility

- [ ] `MaxCachedResponseBodyBytes` configuration option (makes MVP default cap configurable)
- [ ] `Date` header rewriting when serving responses from cache
- [ ] In-app diagnostics view — list unsynced and dead-lettered records
- [ ] Fine-grained per-route policies (TTL, cache strategy, offline response policy)
- [ ] Dead-letter queue management UI (view, requeue, dismiss)

---

## 🥉 v2.0+

> **Goal:** Broader platform support and advanced scenarios

- [ ] `hyperwyc.IndexedDb` — Blazor WASM store provider
- [ ] Additional store providers (`hyperwyc.LiteDb`, `hyperwyc.Sqlite`)
- [ ] User-scoped store — identity-partitioned cache isolation
- [ ] Background sync scheduler — time-based and app-start replay triggers
- [ ] Prefetch on boot or connectivity restoration — pre-warm cache for key routes
- [ ] Smart paging support — cache-aware handling of paginated responses
- [ ] Request grouping and bulk sync — batch multiple queued writes into a single operation
- [ ] GraphQL support — read-intent POST disambiguation

---

## Out of Scope

The following were explored in earlier planning but are intentionally excluded from hyperwyc's scope:

| Item | Reason |
|------|--------|
| Conflict resolution (`IConflictResolver`) | hyperwyc is designed for low-conflict scenarios; resolution is the responsibility of the backend or the consuming application |
| Entity/table synchronisation | hyperwyc operates at the transport layer, not the data model layer |
| Auth / token management | Auth is the responsibility of a separate `DelegatingHandler` in the pipeline |
