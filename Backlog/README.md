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
| ⛔ Superseded | Was implemented, then deliberately removed or replaced; kept for the reasoning |

| Milestone | Meaning |
|---|---|
| v0.1 | Blocks the MVP |
| v1.0 | Blocks calling it a release candidate — deliberately only two items |
| v1.2 | Ergonomics and operational visibility |
| v1.5 | HTTP caching semantics, from the Service Worker audit |
| v2.0+ | Direction, not commitments |

---

## v0.1 (MVP)

| # | Item | Status | Notes |
|---|---|---|---|
| 01 | [Repository & solution setup](Done/01-repo-and-solution-setup.md) | ✅ Done | `hyperwyc.slnx`, two src projects, two test projects |
| 02 | [Core interfaces](Done/02-core-interfaces.md) | ✅ Done | `ISyncStore`, `IConnectivityService`, `ISyncPolicy`, `IStalenessEvaluator`, `IHyperwyc` |
| 03 | [Envelope model](Done/03-envelope-model.md) | ✅ Done | `Envelope` + `CachedResponse` |
| 04 | [`InMemorySyncStore`](Done/04-in-memory-sync-store.md) | ✅ Done | |
| 05 | [`SyncEventStream`](Done/05-sync-event-stream.md) | ✅ Done | Hand-rolled `IObservable<SyncEvent>`; no `System.Reactive` dependency |
| 06 | [Handler — online path](Done/06-hyperwyc-handler-online-path.md) | ✅ Done | |
| 07 | [Handler — offline path](Done/07-hyperwyc-handler-offline-path.md) | ✅ Done | |
| 08 | [Idempotency-Key injection](Done/08-idempotency-key-injection.md) | ⛔ Superseded | Removed by [39](Done/39-reconsider-idempotency.md) |
| 09 | [Response cache for reads](Done/09-response-cache-read-operations.md) | ✅ Done | GET/HEAD/OPTIONS, TTL staleness, body-size cap |
| 10 | [Write-triggered cache invalidation](Done/10-write-triggered-cache-invalidation.md) | ✅ Done | URL-prefix derivation strips trailing id/GUID segments |
| 11 | [Sync flush orchestrator](Done/11-sync-flush-orchestrator.md) | ✅ Done | Debounce + single-flush semaphore |
| 12 | [Polly retry and dead-letter](Done/12-polly-retry-dead-letter.md) | ✅ Done | Retries are in-process within one flush — see item 28 |
| 14 | [`CabinetSyncStore`](Done/14-cabinet-sync-store.md) | ✅ Done | Cabinet 1.0.7, AES-256-GCM at rest |
| 15 | [`AddHyperwyc()` DI extension](Done/15-di-extension-and-options.md) | ✅ Done | |
| 17 | [Max cached body size](Done/17-max-cached-body-size.md) | ✅ Done | Enforced in `HandleOnlineReadAsync`; covered by `ResponseCacheReadTests` |
| 38 | [Retry model: connectivity-driven](Done/38-retry-classification.md) | ✅ Done | One attempt per flush; `4xx` dead-letters at once, `5xx` defers with persisted `RetryCount`/`NextRetryUtc`, a transport failure ends the flush. Made the three orphaned store members live and dropped the `Polly` dependency |
| 34 | [Flush trigger model](Done/34-app-lifecycle-integration.md) | ✅ Done | Documented as a deliberate absence: no lifecycle wiring, and an explicit warning against adding any |
| 35 | [Injectable replay transport](Done/35-orchestrator-transport-not-injectable.md) | ✅ Done | `HyperwycOptions.ReplayTransport`. Never disposed by Hyperwyc; also unblocks 30's auth question |
| 37 | [Replays go through the pipeline](Done/37-replay-through-pipeline.md) | ✅ Done | `HyperwycHandler` steps aside for replays instead of them bypassing the pipeline. Fixes offline writes against authenticated APIs; registration is now `AddHyperwycHandler()` |
| 36 | [Public surface and organisation](Done/36-public-surface.md) | ✅ Done | `IHyperwyc.FlushAsync()` added, `SyncOrchestrator` internal, config enums moved to the root namespace. No `Services/` folder — folders are namespaces here |
| 33 | [Orchestrator disposal](Done/33-orchestrator-sync-disposal.md) | ✅ Done | Both paths now cancel a lifetime token every flush links to. Neither waits for queued work to send; `DisposeAsync` waits only for the in-flight flush to unwind |
| 27 | [`CacheStrategy` never applied](Done/27-cache-strategy-not-applied.md) | ✅ Done | All four presets now honoured on both read paths. Added `X-Hyperwyc-Status: CacheMiss` for a `CacheOnly` read with an empty cache |
| 29 | [Policy TTL not reaching the evaluator](Done/29-default-ttl-propagation.md) | ✅ Done | Also fixed a second defect found alongside it: the default policy's TTL silently overwrote an explicitly set `DefaultCacheTtl` |
| 31 | [Package structure](Done/31-package-structure.md) | ✅ Done | `Hyperwyc` (batteries, Cabinet default) over `Hyperwyc.Core`. Store is a type parameter on `AddHyperwycCore<TStore>()`; `HyperwycOptions.Store` removed |
| 39 | [Idempotency is not Hyperwyc's remit](Done/39-reconsider-idempotency.md) | ✅ Done | Header injection removed; Hyperwyc sends the request the app made and adds nothing. Supersedes 08. Duplicate delivery is a property of retrying in general, resolved between an application and its API |
| 18 | [Sample — product/sales API](Done/18-poc-web-api.md) | ✅ Done | Random catalogue, stock-decrementing sales, `Idempotency-Key` dedup and 400/404/409 failure paths, all verified against a running server |
| 40 | [Surface the outcome of a deferred request](Done/40-surface-deferred-outcomes.md) | ✅ Done | `SyncEvent` gains `CorrelationId`/`RequestId`/`RequestBody`/`Outcome`; `SyncOutcome` is persisted on the envelope so a dead-lettered write explains itself after a restart. Correlation id is the caller's if they set one via `HyperwycRequestOptions.CorrelationId`, otherwise generated and returned on the `202`. Unblocks 19's per-sale status |
| 47 | [Connectivity is required](Done/47-connectivity-is-required.md) | ✅ Done | Ships `NetworkAvailabilityConnectivityService` (BCL-only) **and** removes the `AlwaysOnline` default: a consumer registers an `IConnectivityService` (either side of `AddHyperwyc`) or sets the option, and resolving throws if they do neither. The default failed silently — always-connected means nothing is ever queued or replayed, and the library looks like it works. Narrows 31's zero-config headline, deliberately |
| 13 | [Connectivity documentation](Done/13-connectivity-reference-implementation.md) | ✅ Done | **Scope revised three times, each removing something from the package**: ship a MAUI type → ship a test double → documentation only → documentation with no default ([47](Done/47-connectivity-is-required.md)). README now carries the full `MauiConnectivityService`, the five decisions in it, and how to fake connectivity in your own tests. The sample source carries the same reasoning as comments, since that is what gets copied |
| 51 | [`CabinetSyncStore` was not thread-safe](Done/51-cabinet-store-not-thread-safe.md) | ✅ Done | Crash from the sample: two overlapping saves raced Cabinet's write-temp-then-move and the second `File.Move` threw `FileNotFoundException`. Never synchronised since the file was created; every mutating method was also an unguarded read-modify-write. **Fix confirmed, trigger not explained** — the failure went from never to always without a diff that accounts for it; the leading unproven hypothesis is the resilience handler's per-attempt timeout overlapping a retry with an in-flight cache write |
| 16 | [`ResetStoreAsync()`](Done/16-reset-store-async.md) | ✅ Done | Moved onto `SyncOrchestrator`, which owns the flush gate. Acquires it **blocking** — `FlushAsync`'s try-acquire returns immediately when a flush is running, so the first cut wiped the store underneath one and a deferred envelope was upserted back in afterwards. Reset discards and does not flush: on logout a flush replays through the auth handler the app is revoking, so every write 401s and dead-letters before being wiped anyway |
| **19** | [Sample — .NET MAUI app](Done/19-poc-maui-app.md) | ✅ Done | **Core scenario proven on device:** catalogue served from cache with the network off, across an app restart. |

## v1.0 — release candidate

Deliberately short, and **both are done**. Everything else on the backlog can be worked around by
a consumer; these could not.

| # | Item | Status | Notes |
|---|---|---|---|
| 25 | [Binary request/response bodies](Done/25-binary-request-response-bodies.md) | ✅ Done | Bodies are `byte[]` end to end — `ReadAsByteArrayAsync` in, `ByteArrayContent` out. Round-trip tests cover PNG, gzip and JSON; four of them fail against the old string path. `ByteArrayContent` also stamps no `Content-Type` of its own, which removes the trap that made replayed JSON writes go out as `text/plain`. Cap left at 512 KB, deliberately |
| 49 | [Unreadable store recovery](49-unreadable-store-recovery.md) | ⬜ Open | **Proposed for v1.0, not yet agreed.** A key mismatch throws a raw `CryptographicException` from wherever the store is first touched, including out of the consumer's `HttpClient.SendAsync`. Policy: log, publish an event, degrade to an empty store, stop there — no throw, no delete, no recovery. `ResetStoreAsync` is already the application's remedy. Sequence after [32](32-default-encryption-key.md) |
| 22 | [Per-route policies](Done/22-v1-per-route-policies.md) | ✅ Done | **Net removal**: `ISyncPolicy`, `SyncPolicy`, `PresetSyncPolicy`, `DefaultPolicy` and `DefaultCacheTtl` out; `RoutePolicy` and `RoutePolicyMap` in, with options down to six members. Registration order decides, written general to specific — each rule refines the ones before it, as `.gitignore` and the CSS cascade do. `NetworkOnly` now governs writes too, so an offline write to such a route is declined rather than queued. `ApiFirst` renamed `NetworkFirst` |

## v1.2 — ergonomics and operational visibility

| # | Item | Status | Notes |
|---|---|---|---|
| 32 | [Default encryption key](32-default-encryption-key.md) | 🟡 Partial | Decided: keep the path-derived key as the free default, documented as such. Remaining is the MAUI `SecureStorage` reference implementation |
| 48 | [Exclude the store from OS backup](48-exclude-store-from-os-backup.md) | ⬜ Open | Documentation. The store sits where iOS and Android back it up by default; a **restored outbox replays writes that already happened**, and Hyperwyc has no duplicate suppression by design. Android's 25 MB backup quota is the secondary argument. Path-level exclusion works on both platforms |
| 52 | [Every cache write rewrites the whole store](52-store-rewrites-whole-set-per-write.md) | ⬜ Open | Cabinet's `RecordSet` calls `SaveAllAsync` for a single-record change, so one cached response costs O(total records) to store and filling a cache costs O(n²). Widens every concurrency window as the store grows, which is the leading explanation for [51](Done/51-cabinet-store-not-thread-safe.md)'s never-to-always failure rate. Partly an upstream Cabinet question |
| 53 | [Not AOT-safe: no `JsonSerializerContext`](53-aot-json-serialization.md) | ⬜ Open | `CabinetSyncStore` passes `null` where Cabinet accepts `JsonSerializerOptions`, so serialisation falls back to reflection — in a library whose primary audience ships iOS release builds with AOT on by default. Structurally unfixable by the consumer. **Proposed for v1.0** |
| 21 | [`Date` header rewriting](21-v1-date-header-rewriting.md) | ⬜ Open | Plus the `X-Hyperwyc-Cached-At` header |
| 23 | [Diagnostics view](23-v1-diagnostics-view.md) | ⬜ Open | Read-only outbox and dead-letter queries on `IHyperwyc`. [40](Done/40-surface-deferred-outcomes.md) persisted the failure detail; this is the read path that makes it observable — and the only place a transport failure, which publishes no event, can be seen |
| 24 | [Dead-letter management](24-v1-dead-letter-management.md) | ⬜ Open | Requeue and dismiss. Depends on 23 for the UI surface |
| 20 | [Configurable body cache cap](20-v1-configurable-body-cache-cap.md) | 🟡 Partial | The option is already public and honoured. Remaining: argument validation and README documentation |
| 30 | [Caller-set headers are persisted](30-sensitive-header-exclusion.md) | ⬜ Open | **Reversed to a documentation item.** Stripping them would violate ADR 0001's fidelity obligation and break replay for API keys, basic auth and HMAC — credentials that are still valid at replay time. Document the exposure and point at the encryption key instead |

## v1.5 — HTTP caching semantics

From an audit against Service Worker and Workbox — see
[reference models](../docs/decisions/README.md#reference-models). Suggested order: **41** first,
then **46 before 45**, since cheap revalidation is what makes stale-while-revalidate affordable.

| # | Item | Status | Notes |
|---|---|---|---|
| 41 | [Honour cacheability directives](41-honour-cacheability-directives.md) | ⬜ Open | `Cache-Control: no-store` is ignored and the response written to disk. Service Workers ignore these headers too, but only because you opt in route by route — Hyperwyc caches every GET, so it inherited the stance without the precondition. **The `no-store` sliver alone is small and could be pulled forward** |
| 42 | [Cache grows without bound](42-cache-eviction.md) | ⬜ Open | Individual bodies are capped; the cache as a whole is not. Browsers give you a quota and evict for you — nothing does that here |
| 43 | [`Vary` not honoured](43-honour-vary-header.md) | ⬜ Open | Cache keyed on URL alone, so a content-negotiated endpoint serves the wrong variant. Silent, and looks like a server bug. Sequence with 25 |
| 44 | [No cache generation](44-cache-generation.md) | ⬜ Open | Cached bodies outlive app upgrades, so changed DTO shapes deserialise wrongly. Land with 25 |
| 45 | [`StaleWhileRevalidate`](45-stale-while-revalidate.md) | ⬜ Open | The one Workbox strategy missing. Needs [40](Done/40-surface-deferred-outcomes.md)'s richer `OnUpdated` |
| 46 | [Conditional revalidation](46-conditional-requests.md) | ⬜ Open | `ETag` is already stored and never used, so every refresh re-downloads the whole body |

## v2.0+

| # | Item | Status | Notes |
|---|---|---|---|
| 50 | ["Designing resilient applications with Hyperwyc"](50-resilient-applications-guide.md) | ⬜ Open | Guidance doc, deliberately unscheduled. Collects the scattered "not our remit" caveats into one place and describes the application-owned-store pattern for consumers who need guaranteed delivery. Write it once [22](Done/22-v1-per-route-policies.md) and [25](Done/25-binary-request-response-bodies.md) have stopped moving the surface |
| 26 | [Typed response shaping for offline reads](Done/26-v2-typed-response-shaping.md) | ⛔ Superseded | Closed unbuilt. Layer 0 — return `null` rather than an empty body — shipped separately and removed the sharp edge. Layer 1 (`ReturnsCollection`) is a config knob for a null check the consumer writes anyway, and asks Hyperwyc to know a route returns a collection. Layer 2 depended on Layer 1 |

## Unfiled roadmap items

Listed in [ROADMAP.md](../ROADMAP.md) under v2.0+ with no backlog file yet. Each needs one
before it can be worked on: `Hyperwyc.IndexedDb`, additional store providers (`LiteDb`,
`Sqlite`), user-scoped store, background sync scheduler, prefetch on boot, smart paging,
request grouping / bulk sync, and GraphQL support.

---

## Conventions

- **Scope decisions get an ADR**, not just a backlog item. Anything that constrains what
  Hyperwyc will and will not take responsibility for belongs in
  [docs/decisions](../docs/decisions/README.md) — a `Done/` item stops being read, an ADR is
  meant to be re-read. Run [the scope test](../docs/decisions/README.md#the-standing-scope-test)
  before filing a feature.
- **Public surface: start internal, widen on demand.** Pre-1.0 anything can be made public
  later; nothing can be taken back. See [36](Done/36-public-surface.md).
- **Persisted-format changes are free, for now — but delete and reinstall the sample app.**
  Nothing is published, so no item needs a migration, a compatibility shim or a
  reset-on-upgrade. What that reasoning missed once already
  ([25](Done/25-binary-request-response-bodies.md)) is that a developer device *is* a consumer
  with a live store: a shape change makes the old store undeserialisable and the app throws on
  its first read, which looks like an unrelated HTTP bug. Reinstall after any shape change until
  [49](49-unreadable-store-recovery.md) makes that self-healing. Revisit the whole line the day
  the first package ships.
- **The ADR 0004 audit (2026-08-25) removed all retry apparatus, `IStalenessEvaluator`,
  `OfflineResponsePolicy` and `SyncOutcome.Headers`.** No backlog item: the decision is in
  [ADR 0004](../docs/decisions/0004-default-to-removal.md), the detail is in TECHNICAL_PLAN, and
  the rest is git history. Items 12 and 28 are superseded by it. `SyncOrchestrator` went from 751
  lines to 577, `HyperwycOptions` from eleven knobs to seven, and four public types plus two
  public interface members are gone.
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
- **Item 38's follow-up scheduling bug was caught by its own test**, not by review: a timer
  firing marginally early stranded a deferred envelope. Timing-dependent code earns a repeated
  run before it is called done.
- **Item 35 was found by a test that took 29 seconds** instead of half a second: resolving
  `IHyperwyc` from the container and flushing made a real network call, then passed for the
  wrong reason because dead-lettering also empties the outbox.
- **Item 34 came out of reasoning through 33** on 2026-08-06 and ended in a decision not to
  build anything: shutdown is not a flush trigger. It is kept as an item because the reasoning
  is unintuitive and worth documenting rather than rediscovering.
- **Item 49 is the question item 48 exposed rather than created.** What should happen when the
  store cannot be decrypted was unanswered from the start; the backup finding only supplied a
  likely trigger. Its first draft branched on whether the data was recoverable and had Hyperwyc
  either recreate the store or refuse to start; both were rejected as out of scope. Hyperwyc is
  transport-level and does not promise delivery, so an unreadable store is a fact to report, not
  a problem to solve — log it, raise an event, carry on. The scope correction shrank the item and
  dissolved a dependency on an upstream Cabinet change.
- **Item 51 is what happens when two implementations of one interface are held to different
  standards.** `InMemorySyncStore` serialised everything; `CabinetSyncStore` serialised nothing;
  `ISyncStore` said neither was required. Every store test ran against the safe one and passed,
  and the durable one — the one consumers actually use — corrupted its own file under two
  concurrent requests. A shared conformance suite over both implementations is the structural
  fix and is not yet filed.

  The item also carries a **"what this does not explain"** section. The fix is right, but the
  reported failure rate went from never to always without a diff that accounts for it, and
  writing "fixed" without recording that would have been false confidence. Anything that
  recurs here starts from that section — and from [52](52-store-rewrites-whole-set-per-write.md),
  which came out of it and is the leading explanation.
- **Item 16's bug was hidden by a method name.** Delegating to `FlushAsync` before wiping reads
  as "let the in-flight flush finish", and does not do that — it is a try-acquire that returns
  immediately when a flush is running. The visible symptom would have been rare and awful: a
  deferred envelope written back after the wipe, resurrecting the previous user's queued write
  on logout. Worth remembering that "await the thing" and "await the thing *finishing*" are
  different, and that a semaphore's acquisition mode is part of its contract.
- **Item 48 was filed from a question, and grew a second finding.** The question was about
  backup quota; the answer is that a restored outbox re-sends delivered writes. Writing it up
  also turned up that the path-derived encryption key is not stable across an iOS restore, which
  accidentally mitigates the replay hazard — and stops doing so for exactly the consumers who
  follow item 32's advice and supply their own key. Recorded because inverted risk like that is
  easy to miss.
- **Item 13 never shipped a line of library code, across three scope revisions.** Ship
  `MauiConnectivityService` from core → ship a `StaticConnectivityService` test double →
  documentation only → documentation with connectivity required and no default. Each pass
  removed something from the package. Worth remembering when the next "we should ship a helper
  for this" arrives.
- **Item 22 is the clearest case for the ADR 0004 default.** Adding the last release-candidate
  feature took three public types and two options members *out*. `ISyncPolicy`'s members both
  took an `HttpRequestMessage` no implementation ever read, so ruling per-request configuration
  out of scope did not just settle the design question — it condemned the existing interface. A
  decision to not do something removes the justification for code that already exists, and
  nothing goes back to collect it unless someone looks.
- **Item 40 is where checking the prior art paid and where it didn't.** Service Worker's
  client-messaging pattern — persist the outcome, let the next launch read it — set the design
  order (persisted record first, event derived from it), and Workbox's caller-supplied queue
  `metadata` overturned the planned Hyperwyc-minted id. But Workbox's own `Queue` silently drops
  any request that gets an HTTP error response, which item 38 already handles better. Borrow the
  reasoning, check the behaviour.
- **Item 47 is the counterweight to 31.** Batteries-included picks a store for you because any
  durable store will do; it cannot pick a connectivity source, because that depends on the
  platform and a wrong choice fails invisibly. The rule the two items settle between them:
  *default what you can decide correctly, require what you cannot.* Its second half, found on
  the revision: requiring a decision must not require an ordering, so the check is deferred to
  resolution rather than made at registration.
