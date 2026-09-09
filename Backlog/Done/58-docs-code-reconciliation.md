# Issue 58 — Documentation Describes Behaviour the Code No Longer Has

## Summary

A pass over `docs/` against the code on 2026-09-08 found twelve places where the documentation
describes behaviour that was removed or changed, plus a section that understates the library's
limitations to someone deciding whether to adopt it.

Same shape as [items 27–30](27-cache-strategy-not-applied.md), which were filed from a
docs-vs-code reconciliation on 2026-07-31. Most of these are the residue of two changes that
landed after the affected pages were written: the [ADR 0004](../../docs/decisions/0004-default-to-removal.md)
removal audit, and [ADR 0007](../../docs/decisions/0007-connectivity-cannot-cost-correctness.md)
making any server response final.

## Status

✅ Done, 2026-09-08. Worked removal-first: **six findings were fixed by deleting text rather than
correcting it**, and two tables lost rows they never needed. `docs/delivery.md` is 24 lines
shorter and says more. Finding 9 turned out to be a code question and moved to
[64](../64-correlation-id-missing-on-online-delivery.md).

## Findings

Ordered by consequence, worst first.

| # | Where | What it says | What the code does |
|---|---|---|---|
| 1 | `docs/events.md` — outcome table | `DeliveryOutcomeKind` includes `TransientFailure` (a 5xx/408/429) | The member does not exist. There are three: `Succeeded`, `Rejected`, `TransportFailure`. A consumer who writes the documented `switch` does not compile |
| 2 | `HyperwycOptions.Routes` XML | "matched first-registered-wins", "Register most specific first" | `RoutePolicyMap.PolicyFor` walks the list **backwards** — last registration wins, register general to specific. Following the XML silently applies the wrong policy. Tracked in [61](61-xml-docs-contradict-the-code.md) with the other XML defects, listed here because `delivery.md` is where a reader would check |
| 3 | `docs/delivery.md` — reads table | `CacheFirst()` and `NetworkFirst()` serve a cached response offline "even if stale" | The TTL always applies offline. `ServeReadWithoutNetworkAsync` checks `IsStale` unconditionally; `CacheFirst()` is the default one-day TTL, not the absence of one. Also contradicts the page's own "Understanding TTL" section |
| 4 | `docs/delivery.md` — reads table | `NetworkOnly` offline: "does not return null, allows HttpClient to throw" | An offline **read** on a `NetworkOnly` route gets the `200`/`Offline` response. `responses.md` documents this as the deliberate exception, so the two pages contradict each other |
| 5 | `docs/offline-writes.md` — triggers | Connectivity restored is "debounced by 2 seconds" | There is no debounce; it was removed in the ADR 0004 audit and is named in 0004's own list. `OutboxProcessor` says "No debounce" explicitly. The single-flush gate is what actually absorbs a burst |
| 6 | `docs/pipeline.md` | "one that fails transiently is left queued and tried again at the next opportunity" | No transient class exists. Any server response is final; only a transport failure leaves an envelope queued, and it stops the flush rather than continuing |
| 7 | `docs/events.md` — outcome table | `Rejected` is "a 4xx — it will never work" | `Rejected` is any non-success status including 5xx, 408 and 429. "Will never work" is also the wrong reading: it means Hyperwyc will not try again |
| 8 | `docs/events.md` — sample | The `else` branch handles a transport failure inside an `OnFailed` subscription | Unreachable. `OnFailed` is published only from `DeadLetterAsync`, so `Kind` is always `Rejected`. A transport failure publishes no event at all |
| 9 | `docs/events.md` | "Every event carries a `CorrelationId`" | Null on `OnUpdated` (documented on the type) **and** on the `OnDelivered` published from the online write path, which is constructed with four arguments. A caller who sets `HyperwycRequestOptions.CorrelationId` on a write that goes out online cannot correlate the event. Decide whether that asymmetry is deliberate before rewording |
| 10 | `docs/connectivity.md` ×2 | "silence the startup warning", "not to get past the startup error" | ADR 0007 replaced the throw with one `LogInformation`, whose own comment says it is not a warning that anything is broken |
| 11 | `docs/delivery.md` | "a failure due to a connectivity or **unknown** fault is queued" | `HttpRequestError.Unknown` is explicitly excluded by `NeverReachedTheApi`, along with `InvalidResponse`, `ResponseEnded` and `HttpProtocolError`. Only four errors queue |
| 12 | `docs/events.md` — outcome table | Lists `Headers` as a member of `DeliveryOutcome` | It was removed by the ADR 0004 audit, which names `DeliveryOutcome.Headers` in its list. Same table row as finding 1, so both go in one edit. Found while doing [61](61-xml-docs-contradict-the-code.md) |

## The limitations section understates the limitations

Separate from the list above, and the one most likely to cost a consumer.

`docs/choosing.md` has a **Current limitations** section listing one item, buffered bodies. A
reader evaluating the library reasonably concludes that is the only one. The open backlog says
otherwise, and four of these are consumer-visible and cannot be worked around:

- `Cache-Control` is ignored entirely, including `no-store` ([41](../41-honour-cacheability-directives.md))
- the cache grows without bound; nothing evicts ([42](../42-cache-eviction.md))
- `Vary` is not honoured, so a content-negotiated endpoint serves the wrong variant, silently
  ([43](../43-honour-vary-header.md))
- no `JsonSerializerContext`, so serialisation falls back to reflection — in a library whose
  primary audience ships iOS release builds with AOT on by default ([53](53-aot-json-serialization.md))

These are open items rather than defects, which is exactly the argument for documenting them: a
consumer sizing the library up cannot read the backlog, and 53 in particular belongs in front of
anyone evaluating this for MAUI. `docs/storage.md` is the second place 42 and 53 belong, since
that is the page where both actually bite.

## Also found, smaller

- `docs/responses.md`: "In three cases" above a four-row table; and a fifth case is missing — a
  write whose content cannot be buffered gets no synthetic response either, because
  `TryQueueWriteAsync` returns `null` when `LoadIntoBufferAsync` throws.
- `docs/responses.md`: "For writes, Hyperwyc attaches headers to responses it handles" — the
  `Offline` header goes on reads.
- `docs/connectivity.md`: the `IsConnected` getter is shown as a three-condition expression. It
  is literally `NetworkInterface.GetIsNetworkAvailable()`; the expression is what the BCL does
  inside that call.
- `docs/connectivity.md`: broken anchor `#.net-maui-apps` (GitHub strips the leading dot) and
  `#the-NetworkAvailabilityConnectivityService` (ids are lowercased; fragment matching is
  case-sensitive).
- Typos: `ff you'd rather`, `Hyperwwyc`, `lke`, `syncrhonisation` ×2, `teh`, "a the LICENSE
  file", "the tools using to generate it", "needs to queried", and a repeated clause in
  "Only a request the server actually rejected, after the server refuses".

## What removal did that correction would not have

Applying [ADR 0004](../../docs/decisions/0004-default-to-removal.md) first changed the answer to six
of the twelve, and improved the pages rather than merely repairing them.

| Finding | Correction would have been | What was done instead |
|---|---|---|
| 3, 4 — the reads table | Fix two cells | **Five rows to three.** `CacheFirst(ttl)` and `NetworkFirst(ttl)` are not strategies, they set a TTL; they were duplicating their own parents with one word changed. One sentence now says the overloads exist and the TTL means the same thing offline as online — which is what the wrong cells were groping at |
| — the writes table | n/a | **Deleted.** Five rows, four identical, restating the two sentences directly above it. Its one distinct row — `NetworkOnly` does not queue — moved into the `NetworkOnly` blockquote that was already on the page |
| 1, 7, 12 — the `Kind` row | Rewrite three glosses | Named the three members and dropped the editorialising. "A 4xx — it will never work" was both stale and a claim about the request rather than about Hyperwyc |
| 8 — the unreachable branch | Correct the comment | **Deleted the branch, and the `if` around it.** The snippet lost eight lines and gained a comment saying what `OnFailed` actually means |
| 10 — "the startup warning" | Reword | **Deleted both clauses.** The sentence about registration order was doing the work; the warning half was pre-ADR-0007 residue |
| — "It's a runtime requirement, not a build time requirement" | Reword | **Deleted.** Left over from when resolving without a connectivity service threw. There is no requirement now |
| — the false-positive/negative definitions | Swap the wrong words | **Deleted the definitions.** The table immediately below is unambiguous about which direction is which, and the terms are defined properly further down the page anyway |
| — `responses.md` header lead-in | Fix "for writes" | **Deleted two sentences.** Both were already said, better, three paragraphs up |
| — the fifth no-synthetic-response case | Add a row | **Generalised the existing row.** "The store could not be written" became "the write could not be taken into custody — the store could not be written, or the body could not be read". Same reason, one row |

Also removed under the same test, from the previous pass's conclusion: the VPN/tunnel blockquote
and the `NetworkInterfaceType`-across-platforms paragraph, replaced by one clause in the
when-to-write-your-own guidance.

## What had to be added

Only one thing, and it is the finding that mattered most to a reader deciding whether to adopt:
**`choosing.md`'s Current limitations** now names `Cache-Control` being ignored
([41](../41-honour-cacheability-directives.md)), the unbounded cache
([42](../42-cache-eviction.md)), `Vary` ([43](../43-honour-vary-header.md)) and the missing
`JsonSerializerContext` ([53](53-aot-json-serialization.md)) alongside buffered bodies, with the
AOT one called out as mattering most on iOS.

One sentence was added to `docs/events.md`: nothing at all is published when a delivery attempt
fails at the transport, so silence is what that looks like from the event stream. The page opens
by promising Hyperwyc reports what it did, and this is the case where it does not.

## Acceptance Criteria

- [x] Every row in the findings table is either corrected or, where the code is what should
      change, has its own item.
- [x] `Current limitations` names every open item a consumer cannot work around, and links to it.
- [x] The `NetworkOnly` offline-read behaviour reads the same in `delivery.md` and `responses.md`.
- [x] Anchors resolve — both broken ones fixed, and a link checker over `docs/`, `Backlog/` and
      the README reports zero issues.
- [x] Finding 9 has an answer: it is a code question, not a documentation one, and is now
      [64](../64-correlation-id-missing-on-online-delivery.md). The false claim was removed here.

## Notes

- **Two of these are behaviour changes that outran their documentation rather than drift.**
  Findings 1, 6 and 7 all come from the same change (any server response is final), and 5 from
  the ADR 0004 audit. Worth remembering that removing a concept from the code does not remove it
  from the pages that explained it — the removal audit collected the orphaned *code* and nothing
  went looking for the orphaned prose.
- Finding 2 is the one with a live failure mode attached and the only one where the code is
  right and two separate documents disagree with it in opposite directions.
- Finding 9 may be a code gap rather than a documentation one. Decide before rewording.

- **Removal-first changed the answer to half the findings, and it was not obvious in advance.**
  Filing this item, every row read as "this sentence is wrong, correct it". Asking "can this go?"
  first turned six of them into deletions and took two tables apart. The reads table is the
  clearest case: two of its cells were wrong, and the reason they were wrong is that the table had
  four rows describing two strategies, so the same claim had to be written twice and drifted.
  **Duplication is where staleness accumulates**, which makes the removal test a maintenance
  strategy and not only a design one.
- **The one thing that had to be added was the one a reader would have been most misled by.**
  Everything else was surplus prose describing behaviour that had moved. `Current limitations`
  was the opposite failure — accurate about what it said, and quiet about four things a consumer
  cannot work around.
