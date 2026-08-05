# Hyperwyc — Backlog Index

Every backlog item is a numbered markdown file. Items that are complete move to
[`Done/`](Done); items that are open stay in this folder. This index is the single place to
see what exists, what state it's in, and what order it should be tackled in.

**Status is verified against the code, not against an item's own checkboxes.** Where the two
disagree, this index wins. [ROADMAP.md](../ROADMAP.md) groups the same items by milestone;
[TECHNICAL_PLAN.md](../TECHNICAL_PLAN.md) describes only what is already built.

| Status | Meaning |
|---|---|
| ✅ Done | Implemented and covered by tests |
| 🟡 Partial | Implemented in part; remaining work listed in the notes |
| ⬜ Open | Not started |
| 💭 Under consideration | Captured as a problem statement; not committed to a milestone |

| Priority | Meaning |
|---|---|
| P0 | Blocks the v0.1 MVP |
| P1 | v1.0 — correctness, ergonomics, operational visibility |
| P2 | v2.0+ or speculative |

---

## v0.1 (MVP)

| # | Item | Status | Priority | Notes |
|---|---|---|---|---|
| 01 | [Repository & solution setup](Done/01-repo-and-solution-setup.md) | ✅ Done | P0 | `hyperwyc.slnx`, two src projects, two test projects |
| 02 | [Core interfaces](Done/02-core-interfaces.md) | ✅ Done | P0 | `ISyncStore`, `IConnectivityService`, `ISyncPolicy`, `IStalenessEvaluator`, `IHyperwyc` |
| 03 | [Envelope model](Done/03-envelope-model.md) | ✅ Done | P0 | `Envelope` + `CachedResponse` |
| 04 | [`InMemorySyncStore`](Done/04-in-memory-sync-store.md) | ✅ Done | P0 | |
| 05 | [`SyncEventStream`](Done/05-sync-event-stream.md) | ✅ Done | P0 | Hand-rolled `IObservable<SyncEvent>`; no `System.Reactive` dependency |
| 06 | [Handler — online path](Done/06-hyperwyc-handler-online-path.md) | ✅ Done | P0 | |
| 07 | [Handler — offline path](Done/07-hyperwyc-handler-offline-path.md) | ✅ Done | P0 | |
| 08 | [Idempotency-Key injection](Done/08-idempotency-key-injection.md) | ✅ Done | P0 | |
| 09 | [Response cache for reads](Done/09-response-cache-read-operations.md) | ✅ Done | P0 | GET/HEAD/OPTIONS, TTL staleness, body-size cap |
| 10 | [Write-triggered cache invalidation](Done/10-write-triggered-cache-invalidation.md) | ✅ Done | P0 | URL-prefix derivation strips trailing id/GUID segments |
| 11 | [Sync flush orchestrator](Done/11-sync-flush-orchestrator.md) | ✅ Done | P0 | Debounce + single-flush semaphore |
| 12 | [Polly retry and dead-letter](Done/12-polly-retry-dead-letter.md) | ✅ Done | P0 | Retries are in-process within one flush — see item 28 |
| 14 | [`CabinetSyncStore`](Done/14-cabinet-sync-store.md) | ✅ Done | P0 | Cabinet 1.0.7, AES-256-GCM at rest |
| 15 | [`AddHyperwyc()` DI extension](Done/15-di-extension-and-options.md) | ✅ Done | P0 | |
| 17 | [Max cached body size](Done/17-max-cached-body-size.md) | ✅ Done | P0 | Enforced in `HandleOnlineReadAsync`; covered by `ResponseCacheReadTests` |
| 33 | [Orchestrator disposal](Done/33-orchestrator-sync-disposal.md) | ✅ Done | P0 | Both paths now cancel a lifetime token every flush links to. Neither waits for queued work to send; `DisposeAsync` waits only for the in-flight flush to unwind |
| 27 | [`CacheStrategy` never applied](Done/27-cache-strategy-not-applied.md) | ✅ Done | P0 | All four presets now honoured on both read paths. Added `X-Hyperwyc-Status: CacheMiss` for a `CacheOnly` read with an empty cache |
| 29 | [Policy TTL not reaching the evaluator](Done/29-default-ttl-propagation.md) | ✅ Done | P0 | Also fixed a second defect found alongside it: the default policy's TTL silently overwrote an explicitly set `DefaultCacheTtl` |
| 31 | [Package structure](Done/31-package-structure.md) | ✅ Done | P0 | `Hyperwyc` (batteries, Cabinet default) over `Hyperwyc.Core`. Store is a type parameter on `AddHyperwycCore<TStore>()`; `HyperwycOptions.Store` removed |
| **34** | [Flush trigger model](34-app-lifecycle-integration.md) | ⬜ Open | P0 (docs) | **Decided:** no flush on shutdown or backgrounding, and no lifecycle wiring asked of consumers — queued work implies poor connectivity, which shutting down does not change. Documentation only; also renames the misleading "Lifecycle Events" README heading |
| **13** | [Connectivity reference implementation](13-connectivity-reference-implementation.md) | ⬜ Open | **P0** | Scope revised: `StaticConnectivityService` ships in core; MAUI stays reference code. `MauiConnectivityService` in core is rejected — it would force platform TFMs and a MAUI workload dependency. Blocks 19 |
| **16** | [`ResetStoreAsync()`](16-reset-store-async.md) | 🟡 Partial | P0 | Method exists and delegates to `ISyncStore.ResetAsync`. Missing: flush-semaphore coordination (a reset during an in-flight flush is unguarded) and any unit tests |
| **18** | [POC — ASP.NET Core Web API](18-poc-web-api.md) | ⬜ Open | P0 | No `Hyperwyc.Sample` solution folder exists yet |
| **19** | [POC — .NET MAUI sample app](19-poc-maui-app.md) | ⬜ Open | P0 | Depends on 13 and 18 |

## v1.0

Suggested order: 25 first (breaking store change, best done before there are real users),
then 30, then the ergonomics items.

| # | Item | Status | Priority | Notes |
|---|---|---|---|---|
| 25 | [Binary request/response bodies](25-binary-request-response-bodies.md) | ⬜ Open | **P1 (first)** | Correctness gap, not ergonomics: bodies round-trip through `ReadAsStringAsync`. The item records the decision that no migration is required pre-1.0 |
| 30 | [Sensitive headers are persisted](30-sensitive-header-exclusion.md) | ⬜ Open | P1 | `Authorization` and `Cookie` are stored verbatim and replayed. Includes the open question of how replays acquire fresh credentials |
| 32 | [Default encryption key](32-default-encryption-key.md) | 🟡 Partial | P1 (small) | **Decided:** keep the path-derived key as the free default. README and TECHNICAL_PLAN §9 now state plainly what it does and does not protect. Remaining: the MAUI `SecureStorage` reference implementation, which needs the POC |
| 28 | [Retry state is never persisted](28-persisted-retry-state.md) | ⬜ Open | **P1 (raised)** | `RetryCount` / `NextRetryUtc` / `GetDueForRetryAsync` are built and tested but unused. Decide: persist them, or delete them as dead surface. Priority raised while reasoning through 33/34 — a connectivity-triggered flush cut off by backgrounding discards its budget, so a persistently failing envelope restarts forever instead of converging on dead-letter |
| 22 | [Per-route policies](22-v1-per-route-policies.md) | ⬜ Open | P1 | Largest v1.0 item. Delivers the per-route escape hatches the README already promises. Reuses the strategy resolution added by 27 |
| 21 | [`Date` header rewriting](21-v1-date-header-rewriting.md) | ⬜ Open | P1 | Plus the `X-Hyperwyc-Cached-At` header |
| 23 | [Diagnostics view](23-v1-diagnostics-view.md) | ⬜ Open | P1 | Read-only outbox/dead-letter queries on `IHyperwyc` + a sample page. Reports `RetryCount`, so reads better after 28 |
| 24 | [Dead-letter management](24-v1-dead-letter-management.md) | ⬜ Open | P1 | Requeue/dismiss. Depends on 23 for the UI surface |
| 20 | [Configurable body cache cap](20-v1-configurable-body-cache-cap.md) | 🟡 Partial | P1 (small) | The option is already public and honoured. Remaining: argument validation (negative/zero) and README documentation |

## v2.0+

| # | Item | Status | Priority | Notes |
|---|---|---|---|---|
| 26 | [Typed response shaping for offline reads](26-v2-typed-response-shaping.md) | 💭 Under consideration | P2 | Option A (per-route `EmptyOfflineBody`) may land inside item 22, in which case this closes as a duplicate leaving only the source-generator question |

## Unfiled roadmap items

Listed in [ROADMAP.md](../ROADMAP.md) under v2.0+ with no backlog file yet. Each needs one
before it can be worked on: `Hyperwyc.IndexedDb`, additional store providers (`LiteDb`,
`Sqlite`), user-scoped store, background sync scheduler, prefetch on boot, smart paging,
request grouping / bulk sync, and GraphQL support.

---

## Conventions

- **Numbering is sequential and permanent.** Items keep their number when they move to
  `Done/`; numbers are never reused.
- **A file moves to `Done/` only when its acceptance criteria are ticked and the behaviour
  is covered by tests.** Update this index in the same change.
- **Milestone prefix in the filename** (`-v1-`, `-v2-`) is optional and only used on items
  filed directly against a later milestone. This index is the authority on milestone, not
  the filename.
- **Items 27–30 were filed from a docs-vs-code reconciliation** on 2026-07-31, not from
  feature planning. They record places where the documentation described behaviour the code
  did not have.
- **Items 31–32 record packaging and default-behaviour decisions** taken on 2026-08-02, along
  with the options rejected and why. Item 13 was rewritten in the same pass; its original
  scope (ship `MauiConnectivityService` in core) is recorded in the item as superseded.
- **Item 33 was found while implementing 31.** New registration tests disposed a service
  provider that earlier tests never did, which exposed a latent crash on shutdown.
- **Item 34 came out of reasoning through 33** on 2026-08-06 and ended in a decision not to
  build anything: shutdown is not a flush trigger. It is kept as an item because the reasoning
  is unintuitive and worth documenting rather than rediscovering.
