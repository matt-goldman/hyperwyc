# Hyperwyc — Backlog Index

Every backlog item is a numbered markdown file. Items that are complete move to
[`Done/`](Done); items that are open stay in this folder. This index is the single place to
see what exists, what state it's in, and what order it should be tackled in.

**Status is verified against the code, not against an item's own checkboxes.** Where the two
disagree, this index wins. It is also the only forward-looking document: `ROADMAP.md` was a
second, staler copy of these milestones and was removed on 2026-09-05, along with
`TECHNICAL_PLAN.md`, whose as-built detail had drifted into a duplicate of the code comments.
What was worth keeping from them moved into [docs](../docs/) and
[docs/decisions](../docs/decisions/); the rest is in git history.

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
| 02 | [Core interfaces](Done/02-core-interfaces.md) | ✅ Done | `IHyperwycStore`, `IConnectivityService`, `ISyncPolicy`, `IStalenessEvaluator`, `IHyperwyc` |
| 03 | [Envelope model](Done/03-envelope-model.md) | ✅ Done | `Envelope` + `CachedResponse` |
| 04 | [`InMemoryStore`](Done/04-in-memory-sync-store.md) | ✅ Done | |
| 05 | [`HyperwycEventStream`](Done/05-sync-event-stream.md) | ✅ Done | Hand-rolled `IObservable<HyperwycEvent>`; no `System.Reactive` dependency |
| 06 | [Handler — online path](Done/06-hyperwyc-handler-online-path.md) | ✅ Done | |
| 07 | [Handler — offline path](Done/07-hyperwyc-handler-offline-path.md) | ✅ Done | |
| 08 | [Idempotency-Key injection](Done/08-idempotency-key-injection.md) | ⛔ Superseded | Removed by [39](Done/39-reconsider-idempotency.md) |
| 09 | [Response cache for reads](Done/09-response-cache-read-operations.md) | ✅ Done | GET/HEAD/OPTIONS, TTL staleness, body-size cap |
| 10 | [Write-triggered cache invalidation](Done/10-write-triggered-cache-invalidation.md) | ✅ Done | URL-prefix derivation strips trailing id/GUID segments |
| 11 | [Sync flush orchestrator](Done/11-sync-flush-orchestrator.md) | ✅ Done | Debounce + single-flush semaphore |
| 12 | [Polly retry and dead-letter](Done/12-polly-retry-dead-letter.md) | ✅ Done | Retries are in-process within one flush — see item 28 |
| 14 | [`CabinetStore`](Done/14-cabinet-sync-store.md) | ✅ Done | Cabinet 1.0.7, AES-256-GCM at rest |
| 15 | [`AddHyperwyc()` DI extension](Done/15-di-extension-and-options.md) | ✅ Done | |
| 17 | [Max cached body size](Done/17-max-cached-body-size.md) | ✅ Done | Enforced in `HandleOnlineReadAsync`; covered by `ResponseCacheReadTests` |
| 38 | [Retry model: connectivity-driven](Done/38-retry-classification.md) | ✅ Done | One attempt per flush; `4xx` dead-letters at once, `5xx` defers with persisted `RetryCount`/`NextRetryUtc`, a transport failure ends the flush. Made the three orphaned store members live and dropped the `Polly` dependency |
| 34 | [Flush trigger model](Done/34-app-lifecycle-integration.md) | ✅ Done | Documented as a deliberate absence: no lifecycle wiring, and an explicit warning against adding any |
| 35 | [Injectable replay transport](Done/35-orchestrator-transport-not-injectable.md) | ✅ Done | `HyperwycOptions.ReplayTransport`. Never disposed by Hyperwyc; also unblocks 30's auth question |
| 37 | [Replays go through the pipeline](Done/37-replay-through-pipeline.md) | ✅ Done | `HyperwycHandler` steps aside for replays instead of them bypassing the pipeline. Fixes offline writes against authenticated APIs; registration is now `AddHyperwycHandler()` |
| 36 | [Public surface and organisation](Done/36-public-surface.md) | ✅ Done | `IHyperwyc.FlushAsync()` added, `OutboxProcessor` internal, config enums moved to the root namespace. No `Services/` folder — folders are namespaces here |
| 33 | [Orchestrator disposal](Done/33-orchestrator-sync-disposal.md) | ✅ Done | Both paths now cancel a lifetime token every flush links to. Neither waits for queued work to send; `DisposeAsync` waits only for the in-flight flush to unwind |
| 27 | [`SourcePriority` never applied](Done/27-cache-strategy-not-applied.md) | ✅ Done | All four presets now honoured on both read paths. Added `X-Hyperwyc-Status: CacheMiss` for a `CacheOnly` read with an empty cache |
| 29 | [Policy TTL not reaching the evaluator](Done/29-default-ttl-propagation.md) | ✅ Done | Also fixed a second defect found alongside it: the default policy's TTL silently overwrote an explicitly set `DefaultCacheTtl` |
| 31 | [Package structure](Done/31-package-structure.md) | ✅ Done | `Hyperwyc` (batteries, Cabinet default) over `Hyperwyc.Core`. Store is a type parameter on `AddHyperwycCore<TStore>()`; `HyperwycOptions.Store` removed |
| 39 | [Idempotency is not Hyperwyc's remit](Done/39-reconsider-idempotency.md) | ✅ Done | Header injection removed; Hyperwyc sends the request the app made and adds nothing. Supersedes 08. Duplicate delivery is a property of retrying in general, resolved between an application and its API |
| 18 | [Sample — product/sales API](Done/18-poc-web-api.md) | ✅ Done | Random catalogue, stock-decrementing sales, `Idempotency-Key` dedup and 400/404/409 failure paths, all verified against a running server |
| 40 | [Surface the outcome of a deferred request](Done/40-surface-deferred-outcomes.md) | ✅ Done | `HyperwycEvent` gains `CorrelationId`/`RequestId`/`RequestBody`/`Outcome`; `DeliveryOutcome` is persisted on the envelope so a dead-lettered write explains itself after a restart. Correlation id is the caller's if they set one via `HyperwycRequestOptions.CorrelationId`, otherwise generated and returned on the `202`. Unblocks 19's per-sale status |
| 47 | [Connectivity is required](Done/47-connectivity-is-required.md) | ✅ Done | Ships `NetworkAvailabilityConnectivityService` (BCL-only) **and** removes the `AlwaysOnline` default: a consumer registers an `IConnectivityService` (either side of `AddHyperwyc`) or sets the option, and resolving throws if they do neither. The default failed silently — always-connected means nothing is ever queued or replayed, and the library looks like it works. Narrows 31's zero-config headline, deliberately |
| 13 | [Connectivity documentation](Done/13-connectivity-reference-implementation.md) | ✅ Done | **Scope revised three times, each removing something from the package**: ship a MAUI type → ship a test double → documentation only → documentation with no default ([47](Done/47-connectivity-is-required.md)). README now carries the full `MauiConnectivityService`, the five decisions in it, and how to fake connectivity in your own tests. The sample source carries the same reasoning as comments, since that is what gets copied |
| 51 | [`CabinetStore` was not thread-safe](Done/51-cabinet-store-not-thread-safe.md) | ✅ Done | Crash from the sample: two overlapping saves raced Cabinet's write-temp-then-move and the second `File.Move` threw `FileNotFoundException`. Never synchronised since the file was created; every mutating method was also an unguarded read-modify-write. **Fix confirmed, trigger not explained** — the failure went from never to always without a diff that accounts for it; the leading unproven hypothesis is the resilience handler's per-attempt timeout overlapping a retry with an in-flight cache write |
| 16 | [`ResetStoreAsync()`](Done/16-reset-store-async.md) | ✅ Done | Moved onto `OutboxProcessor`, which owns the flush gate. Acquires it **blocking** — `FlushAsync`'s try-acquire returns immediately when a flush is running, so the first cut wiped the store underneath one and a deferred envelope was upserted back in afterwards. Reset discards and does not flush: on logout a flush replays through the auth handler the app is revoking, so every write 401s and dead-letters before being wiped anyway |
| **19** | [Sample — .NET MAUI app](Done/19-poc-maui-app.md) | ✅ Done | **Core scenario proven on device:** catalogue served from cache with the network off, across an app restart. |

## v1.0 — release candidate

Deliberately short, and **both are done**. Everything else on the backlog can be worked around by
a consumer; these could not.

| # | Item | Status | Notes |
|---|---|---|---|
| 25 | [Binary request/response bodies](Done/25-binary-request-response-bodies.md) | ✅ Done | Bodies are `byte[]` end to end — `ReadAsByteArrayAsync` in, `ByteArrayContent` out. Round-trip tests cover PNG, gzip and JSON; four of them fail against the old string path. `ByteArrayContent` also stamps no `Content-Type` of its own, which removes the trap that made replayed JSON writes go out as `text/plain`. Cap left at 512 KB, deliberately |
| 49 | [Unreadable store recovery](Done/49-unreadable-store-recovery.md) | ✅ Done | Reports and steps aside: logs through an optional `ILogger`, publishes `OnStoreUnreadable` once, and passes every request through thereafter. Nothing deleted, nothing thrown, and **no `202` for a write it cannot hold**. `CabinetStore.ResetAsync` now clears files rather than enumerating records, so the remedy works in the case that needs it |
| 22 | [Per-route policies](Done/22-v1-per-route-policies.md) | ✅ Done | **Net removal**: `ISyncPolicy`, `SyncPolicy`, `PresetSyncPolicy`, `DefaultPolicy` and `DefaultCacheTtl` out; `RoutePolicy` and `RoutePolicyMap` in, with options down to six members. Registration order decides, written general to specific — each rule refines the ones before it, as `.gitignore` and the CSS cascade do. `NetworkOnly` now governs writes too, so an offline write to such a route is declined rather than queued. `ApiFirst` renamed `NetworkFirst` |

## v1.2 — ergonomics and operational visibility

| # | Item | Status | Notes |
|---|---|---|---|
| 32 | [Default encryption key](32-default-encryption-key.md) | 🟡 Partial | Decided: keep the path-derived key as the free default, documented as such. Remaining is the MAUI `SecureStorage` reference implementation |
| 48 | [Exclude the store from OS backup](Done/48-exclude-store-from-os-backup.md) | ✅ Done | Documentation, and it is written: [storage.md](../docs/storage.md#excluding-the-store-from-os-backup) leads with the one sentence that matters — a restored backup re-sends writes that already happened — plus the iOS and Android mechanisms, the cloud-backup/device-transfer split, links to both vendors' docs, and the 25 MB quota. [maui.md](../docs/maui.md) carries the short version for the audience most exposed to it |
| 52 | [Every cache write rewrites the whole store](Done/52-store-rewrites-whole-set-per-write.md) | ⛔ Superseded | Cabinet's `RecordSet` calls `SaveAllAsync` for a single-record change, so filling a cache costs O(n²). Unchanged by Cabinet 2.0. **Diagnosis holds; the prescribed fix was refused** — Cabinet had already measured per-file storage at ~500× slower than one aggregate file, and n's weight here is *bytes*, not records: base64 bodies inside the record are essentially all of it. Superseded by [70](Done/70-move-bodies-to-cabinet-attachments.md) + [42](42-cache-eviction.md) under [ADR 0008](../docs/decisions/0008-shrink-what-you-store.md) |
| 53 | [Not AOT-safe: no `JsonSerializerContext`](Done/53-aot-json-serialization.md) | ✅ Done | An internal `HyperwycJsonContext` on `List<Envelope>`, passed through Cabinet's four-argument constructor — the bare `null` had been binding to the `IIndexProvider?` overload, so "no options" silently meant "default options". `IsAotCompatible` on both src projects found a second, unrelated defect on the first build: `AddHyperwycCore<TStore>` let the trimmer remove the constructor the container looks for. **Held by turning off System.Text.Json's reflection fallback in `Hyperwyc.Tests`** — without that the round-trip tests pass either way, which is the whole shape of this bug. Cabinet 2.0 contributed nothing here; the seam is the same one 1.x had. The docs' AOT caveats are gone |
| 55 | [`Envelope.IsSynced` distinguishes two kinds by negation](55-envelope-kind-discriminator.md) | ⬜ Open | `Envelope` is a cached response *and* a queued write, told apart by a flag set `true` on responses that were never synced anywhere. The vocabulary pass could not rename it — `IsDelivered` would be actively false — which is the tell that the model, not the name, is wrong. **[66](66-dead-letter-store-fails-the-scope-test.md) has landed, so this is next**: it removed two of the flag's three jobs and leaves only *this is a cache entry* — and which inverts the choice here, because four of the six remaining store methods are already kind-specific, so splitting `UpsertAsync` makes the interface more honest rather than more complicated |
| 21 | [`Date` header rewriting](21-v1-date-header-rewriting.md) | ⬜ Open | Plus the `X-Hyperwyc-Cached-At` header, which may dissolve [54](Done/54-ttl-as-a-validity-bound.md) |
| 54 | [TTL is a validity bound](Done/54-ttl-as-a-validity-bound.md) | ⛔ Closed | Filed and dissolved the same day. Rather than adding a way to express "must not be served stale", the refetch-trigger reading was removed: TTL now means how old a response may be and still be served, online and offline alike. Default raised from 5 minutes to 1 day to suit. "Fetch fresh when possible" was always `NetworkFirst` |
| 23 | [Diagnostics view](23-v1-diagnostics-view.md) | ⬜ Open | Read-only outbox queries on `IHyperwyc`. **Narrowed by [66](66-dead-letter-store-fails-the-scope-test.md), not dissolved** — the dead-letter half is gone with the store, and what is left is genuinely ours: the outbox passes scope-test Q4 by name. It is also the only place a transport failure, which publishes no event, can be seen, and since 66 that is the only outcome still written down |
| 24 | [Dead-letter management](24-v1-dead-letter-management.md) | ⛔ Closed | **Dissolved, not superseded** — never built, so there is nothing to supersede. Condemned by the vocabulary finding rather than by [66](66-dead-letter-store-fails-the-scope-test.md), so it holds under every branch of that decision: dead-lettering means *could not be delivered*, and this managed things that had been. Its halves went to different homes, which is the tell they were bundled by the wrong model — **requeue** to [69](69-expiring-queued-writes.md) (only coherent for something never delivered), **dismiss** to [67](67-configurable-response-retention.md) (a retention decision). Also independently stale: it still classified on `4xx` except 408/429, and reset `RetryCount`/`NextRetryUtc`, neither of which has existed since the ADR 0004 audit |
| 20 | [Configurable body cache cap](20-v1-configurable-body-cache-cap.md) | 🟡 Partial | The option is already public and honoured. Remaining: argument validation and README documentation |
| 30 | [Caller-set headers are persisted](30-sensitive-header-exclusion.md) | ⬜ Open | **Reversed to a documentation item.** Stripping them would violate ADR 0001's fidelity obligation and break replay for API keys, basic auth and HMAC — credentials that are still valid at replay time. Document the exposure and point at the encryption key instead. **[66](66-dead-letter-store-fails-the-scope-test.md) shortens it for outbox entries** — headers would persist only while a write is outstanding rather than indefinitely — but not for cache entries, which keep the headers of the `GET` that made them. Say which is which |
| 58 | [Docs described behaviour the code no longer had](Done/58-docs-code-reconciliation.md) | ✅ Done | Twelve findings; **six fixed by deleting text rather than correcting it**, and two tables lost rows they never needed — the reads table went five rows to three, the writes table went entirely. `delivery.md` is 24 lines shorter and says more. Only one addition was needed, and it was the one that mattered: `choosing.md`'s **Current limitations** listed buffered bodies alone where [41](41-honour-cacheability-directives.md), [42](42-cache-eviction.md), [43](43-honour-vary-header.md) and [53](Done/53-aot-json-serialization.md) are all consumer-visible and unworkaroundable. **Duplication is where staleness accumulated** — the two wrong cells were wrong because one table described two strategies in four rows |
| 61 | [XML documentation contradicted the code](Done/61-xml-docs-contradict-the-code.md) | ✅ Done | Seventeen corrections across seven files; builds clean with cref warnings as errors. `HyperwycOptions.Routes` documented the opposite matching order to the one `RoutePolicyMap` implements, so following it applied the wrong policy silently; `ReplayTransport` still said replays bypass the pipeline, which [37](Done/37-replay-through-pipeline.md) and ADR 0002 reversed. **Reading the generated XML in order found five findings the targeted grep missed** — "successfully synced", "after all retry attempts have been exhausted" — because they used no distinctive stale vocabulary. Two were on `IHyperwycStore`, the interface a consumer implements. `Envelope.IsSynced` was asserting as fact the meaning [55](55-envelope-kind-discriminator.md) exists to say it does not have |
| 59 | [Events could not be consumed without System.Reactive](Done/59-events-without-system-reactive.md) | ✅ Done | **Two snippets, not one.** `Subscribe(Action<T>)` is an Rx extension too — `IObservable<T>` declares only `Subscribe(IObserver<T>)` — so the page's opening one-liner did not compile either, which is the worse of the two since a reader hits it first. Confirmed with `error CS1660` rather than assumed. Opening example is now a plain `IObserver`; the outcome example keeps Rx, labelled, because that is what most apps want |
| 56 | [Documentation restructure](Done/56-documentation-restructure.md) | ✅ Done | Four new documents — a five-page [tutorial](../docs/tutorial/), a [MAUI quick start](../docs/maui.md), [design](../docs/design.md) for the explanation layer, and [testing](../docs/testing.md) — with six pages stripped back to reference and the nav rebuilt around the four readers. `connectivity.md` went 337 → 146 lines without losing anything. **The tutorial was run before it was written**, and doing so changed its spine: stopping the API does not make the connectivity service report offline, so pages 1–3 demonstrate the ADR 0007 transport-failure path and page 4 arrives as "here is what the zero-config path cost you". Written from the design instead, it would have credited the connectivity service with work it was not doing. Patterns deliberately not written — that is [50](50-resilient-applications-guide.md), already specified in more detail than a restructure would produce |
| 57 | [`Plugin.Maui.Hyperwyc`](57-plugin-maui-hyperwyc.md) | 💭 Under consideration | MAUI is the core use case and has the worst onboarding path — copy a 100-line class out of a 311-line page. A MAUI package would also house 32's `SecureStorage` key and 48's backup exclusion. **Answer the ADR question first**: a meta extension method on `MauiAppBuilder` registers a connectivity source on the consumer's behalf, which is the thing ADR 0006 says a shipped implementation is not. Read [13](Done/13-connectivity-reference-implementation.md) first — it declined to ship this type three times |
| 60 | [Glossary](60-glossary.md) | ⬜ Open | Outbox, envelope, replay, flush, dead-letter, correlation id, synthetic response, stale. **The dead-letter entry may dissolve rather than get written**: the two terminal states differ in *what is kept*, not in whether Hyperwyc succeeded — a `409` and a `201` both reached the API, which is the whole promise. "Dead-letter" is a message-queue borrowing meaning *we gave up*, where here it means *delivered, answer kept for you*. Renaming it is the ADR 0004 answer; the entry is whatever survives. Touches `MoveToDeadLetterAsync`, `IsDeadLettered`, `OnFailed`, `Rejected`, all public — and tidies [24](24-v1-dead-letter-management.md) |
| 62 | [Non-destructive reset when the store cannot be read](62-reset-store-on-failure.md) | 💭 Under consideration | **Direction agreed; the bound is the open question.** Move the unreadable store aside — a `-1` suffix or a quarantine sibling — and start clean, so nothing is deleted and functionality resumes. Filed with the opposite recommendation and reversed the same day: today's behaviour preserves forensics, not delivery (the `202` was already returned), and charges a working app for bytes on an end user's device that nobody will ever reach. **Technically correct, pragmatically wrong**, and `= false` would not fix it because a default nobody changes is the behaviour that ships. Leading candidate for the accumulation problem is *quarantine once* — the existence of the orphan is the counter, a second failure is systemic rather than incidental, and the bound comes free. Probably needs no option at all. Watch the key-derivation trap: the orphan's ciphertext was written under the old path's key. This is the self-healing half of [49](Done/49-unreadable-store-recovery.md) that never shipped |
| 63 | [Configurable synthetic response codes](63-configurable-synthetic-responses.md) | 💭 Under consideration | **Recommendation: no.** The `202` is the only in-band signal separating a queued write from a delivered one, and a knob lets a consumer configure it away silently. For the `200`, a configured `404` re-arms the exception the `null` body exists to remove. Item 26's shape, reached the same way. Answers both TODOs in `responses.md` |
| 64 | [Correlation id missing on online delivery](64-correlation-id-missing-on-online-delivery.md) | 💭 Under consideration | `HyperwycRequestOptions.CorrelationId` reaches the events for a queued write and not the `OnDelivered` published when a write goes out online, which is constructed with four arguments. So an app reconciling purely from `Events` — the pattern [50](50-resilient-applications-guide.md) recommends — has a hole in the happy path. Arguable: the online caller already has the real response synchronously and never needed an event. Found from a false claim in `docs/events.md` while doing [58](Done/58-docs-code-reconciliation.md) |
| 65 | [Startup flush requires a host](65-startup-flush-requires-a-host.md) | ⬜ Open | **Blunted by [66](66-dead-letter-store-fails-the-scope-test.md), which flipped the default to `false`** — a trigger nobody is relying on cannot silently fail to fire. What is left is real and still wants doing: the docs must say it needs a host, and the MAUI question needs a device. Original finding follows. `FlushOnStartup` defaulted to `true` and is an `IHostedService`, so it never fires in an app built on a bare `ServiceCollection`. Measured: same store, API up, queued write — plain provider delivers nothing, real `Host` delivers. Nothing is lost (the connectivity trigger and `FlushAsync()` still work) but a documented trigger is silently absent. **The open question is MAUI**: `MauiApp` may not start hosted services, in which case `FlushOnStartup` has never fired there and every queued write has gone out on reconnect alone — invisible, because that looks identical from outside. Needs a device to answer; a reason for [57](57-plugin-maui-hyperwyc.md) |
| 66 | [The dead-letter store fails the scope test](66-dead-letter-store-fails-the-scope-test.md) | 🟡 Built, ADR pending | **Decided as B — retain nothing once delivered — and built; [ADR 0010](../docs/decisions/0010-retain-only-outstanding-work.md) is `Proposed`, so this stays open until it is accepted.** `MoveToDeadLetterAsync`, `IsDeadLettered` and `OnFailed` are gone; `Succeeded`/`Rejected` collapse into `Delivered`; `MarkDeliveredAsync` becomes `RemoveDeliveredAsync` and deletes the record, request body and headers with it. **`FlushOnStartup` now defaults to `false`** — the event is the only report of a delivery, so a flush inside host startup would race the subscriber and lose it silently. The `409`-body cost is accepted rather than solved, and filed as [67](67-configurable-response-retention.md). The original argument follows. **Scope decision — needs an ADR.** Hyperwyc succeeds or fails at *delivery*; a `201` and a `409` are the same event from its side. Yet a non-`2xx` is retained indefinitely and a `2xx` discarded — an asymmetry only coherent if a non-`2xx` is a *Hyperwyc* failure, which it is not. Scope test Q4 draws the line: the **event** passes (only Hyperwyc can join this response to a caller who got a `202` and moved on), the **store** fails (what remains is an ordinary HTTP response the app can file under its own correlation id). Would remove `MoveToDeadLetterAsync`, `IsDeadLettered`, `OnFailed`, and dissolve [24](24-v1-dead-letter-management.md). Honest tension: a `409` body is unrecoverable by re-reading |
| 67 | [Configurable response retention](67-configurable-response-retention.md) | 💭 Under consideration | The deliberate later answer to [66](66-dead-letter-store-fails-the-scope-test.md)'s one real cost: a rejection often carries the only explanation of itself. The criterion is not a status range but **did the server tell you something you cannot get any other way** — `400`/`409`/`422` yes, `401`/`403`/`404` usually not, `429` yes but in a *header*. That last one reopens `DeliveryOutcome.Headers`, removed by the ADR 0004 audit, so retention and headers are one decision. Opt-in, empty by default, eviction that never touches unsent work, responses only. **Filed so the absence stays a decision rather than an oversight** — closed unbuilt would be a success |
| 68 | [Cache invalidation leaves tombstones](Done/68-cache-invalidation-leaves-tombstones.md) | ✅ Done | Invalidation nulled the `Response` and wrote the husk back — a record no query returns and nothing deletes, holding its `Authorization` header indefinitely. Now removes the record. **Both candidate fixes taken, and the premise that deleting needed a public interface change was wrong**: `InvalidateCacheForPrefixAsync` is already a store method, and its own summary already said "Removes". Filtering to entries that have a `Response` went from waste-avoidance to a precondition — an unfiltered *delete* would take queued writes with it. Sequenced before [70](Done/70-move-bodies-to-cabinet-attachments.md) so Cabinet's `RemoveAsync` cleans up the bodies for free |
| 69 | [Should a queued write expire?](69-expiring-queued-writes.md) | 💭 Under consideration | Hyperwyc holds an undelivered write forever — a month offline means a month-old order goes out on reconnect, and `storage.md` already names that as *worse* than a duplicate while offering nothing against it. **Dead-letter was misapplied, not misjudged**: in a bus it means *could not be delivered*, and it was being used for things that had been. Freed of that, the real question appears — and it **passes Q4 where [66](66-dead-letter-store-fails-the-scope-test.md) failed it**, because the age of something in the outbox is exactly what only Hyperwyc knows. Age yes, attempts no — an attempt count counts times the device regained connectivity, so ten reconnects on a train and ten over ten days are the same number and different situations; and a transport failure stops the flush, so counts do not accrue comparably across the queue. Off by default, loud, and not on `Ttl` |
| 70 | [Move bodies to Cabinet attachments](Done/70-move-bodies-to-cabinet-attachments.md) | ✅ Done | Bodies are attachments now, not base64 inside the rewritten document; a 256 KB cached body leaves the record file under 8 KB. Reads hydrate a **copy** — `RecordSet` writes back the instances it hands out, so hydrating in place would have put the bodies back into the document on a later unrelated write. `GetCachedResponseAsync` hydrates one match after filtering; the outbox hydrates all of its (bounded) set. The benchmark inherited from [52](Done/52-store-rewrites-whole-set-per-write.md) moved to [71](71-store-benchmarks-and-alternative-implementations.md) — it measures the store, not this change |
| 71 | [Benchmark the store, and build the alternatives](71-store-benchmarks-and-alternative-implementations.md) | ⬜ Open | No numbers for the storage layer, and no defensible answer to "why Cabinet and not the thing I already use". **Low priority, and not on anyone's critical path.** One caveat: [55](55-envelope-kind-discriminator.md) and [66](66-dead-letter-store-fails-the-scope-test.md) may reshape `IHyperwycStore`, so the LiteDB and SQLite implementations would be written twice if started while either is live — the benchmarks themselves are unaffected. Not to be confused with the conformance suite, which is a different artefact recorded in [51](Done/51-cabinet-store-not-thread-safe.md) |

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

Direction rather than commitments, with no backlog file yet. Each needs one
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
  [49](Done/49-unreadable-store-recovery.md) makes that self-healing. Revisit the whole line the day
  the first package ships.
- **The ADR 0004 audit (2026-08-25) removed all retry apparatus, `IStalenessEvaluator`,
  `OfflineResponsePolicy` and `DeliveryOutcome.Headers`.** No backlog item: the decision is in
  [ADR 0004](../docs/decisions/0004-default-to-removal.md), which records what came out, and
  the rest is git history. Items 12 and 28 are superseded by it. `OutboxProcessor` went from 751
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
- **Items 56–63 came from the same exercise repeated on 2026-09-08**, ahead of the first publish,
  and found the same class of thing in a second place: not only `docs/` but the XML documentation
  that ships inside the package ([61](Done/61-xml-docs-contradict-the-code.md)). Two patterns are
  worth naming. First, **removing a concept from the code does not remove it from the prose that
  explained it** — the ADR 0004 audit collected the orphaned apparatus and nothing went looking
  for the orphaned sentences, so a debounce, a retry budget and a transient failure class all
  still exist in writing. Second, **the documentation had drifted into being wrong in the
  reader's favour**: `Current limitations` lists one limitation where four open items are
  consumer-visible and cannot be worked around.
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
  standards.** `InMemoryStore` serialised everything; `CabinetStore` serialised nothing;
  `IHyperwycStore` said neither was required. Every store test ran against the safe one and passed,
  and the durable one — the one consumers actually use — corrupted its own file under two
  concurrent requests. A shared conformance suite over both implementations is the structural
  fix and is not yet filed.

  The item also carries a **"what this does not explain"** section. The fix is right, but the
  reported failure rate went from never to always without a diff that accounts for it, and
  writing "fixed" without recording that would have been false confidence. Anything that
  recurs here starts from that section — and from [52](Done/52-store-rewrites-whole-set-per-write.md),
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
- **The vocabulary pass (2026-09-05) renamed `sync` out and kept `cache`.** "Sync" named the
  bidirectional-with-conflict-resolution category the README spends paragraphs distancing us
  from; the write path is an outbox and a delivery, and that vocabulary was already in the code.
  "Cache" stayed because every use of it was genuine caching — the problem was that it was the
  *only* word, so it got stretched over the write path. `CacheStrategy` became `SourcePriority`,
  which names the relationship between sources rather than either side of it. **Items in `Done/`
  keep the old vocabulary**: they record what was decided when, and rewriting them would falsify
  the record.
- **`CacheOnly` shipped unusable and was removed on 2026-09-05.** It returned early before the
  caching step, so a route configured with it could never populate its own cache and missed on
  every call forever. Workbox's version works because precaching fills the store at install
  time — our prefetch-on-boot, still unbuilt. We shipped the consumer half of a two-part
  mechanism and nothing caught it, because every test seeded the cache by hand. Worth
  remembering: a feature whose only sensible use depends on an unbuilt feature is not a feature.
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
