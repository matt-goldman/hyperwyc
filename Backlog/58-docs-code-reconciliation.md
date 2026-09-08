# Issue 58 — Documentation Describes Behaviour the Code No Longer Has

## Summary

A pass over `docs/` against the code on 2026-09-08 found twelve places where the documentation
describes behaviour that was removed or changed, plus a section that understates the library's
limitations to someone deciding whether to adopt it.

Same shape as [items 27–30](Done/27-cache-strategy-not-applied.md), which were filed from a
docs-vs-code reconciliation on 2026-07-31. Most of these are the residue of two changes that
landed after the affected pages were written: the [ADR 0004](../docs/decisions/0004-default-to-removal.md)
removal audit, and [ADR 0007](../docs/decisions/0007-connectivity-cannot-cost-correctness.md)
making any server response final.

## Status

⬜ Open. Filed 2026-09-08. **Blocks first publish** — the package and the repo go public together,
and two of these mislead a consumer into writing code that will not compile or will not work.

## Findings

Ordered by consequence, worst first.

| # | Where | What it says | What the code does |
|---|---|---|---|
| 1 | `docs/events.md` — outcome table | `DeliveryOutcomeKind` includes `TransientFailure` (a 5xx/408/429) | The member does not exist. There are three: `Succeeded`, `Rejected`, `TransportFailure`. A consumer who writes the documented `switch` does not compile |
| 2 | `HyperwycOptions.Routes` XML | "matched first-registered-wins", "Register most specific first" | `RoutePolicyMap.PolicyFor` walks the list **backwards** — last registration wins, register general to specific. Following the XML silently applies the wrong policy. Tracked in [61](Done/61-xml-docs-contradict-the-code.md) with the other XML defects, listed here because `delivery.md` is where a reader would check |
| 3 | `docs/delivery.md` — reads table | `CacheFirst()` and `NetworkFirst()` serve a cached response offline "even if stale" | The TTL always applies offline. `ServeReadWithoutNetworkAsync` checks `IsStale` unconditionally; `CacheFirst()` is the default one-day TTL, not the absence of one. Also contradicts the page's own "Understanding TTL" section |
| 4 | `docs/delivery.md` — reads table | `NetworkOnly` offline: "does not return null, allows HttpClient to throw" | An offline **read** on a `NetworkOnly` route gets the `200`/`Offline` response. `responses.md` documents this as the deliberate exception, so the two pages contradict each other |
| 5 | `docs/offline-writes.md` — triggers | Connectivity restored is "debounced by 2 seconds" | There is no debounce; it was removed in the ADR 0004 audit and is named in 0004's own list. `OutboxProcessor` says "No debounce" explicitly. The single-flush gate is what actually absorbs a burst |
| 6 | `docs/pipeline.md` | "one that fails transiently is left queued and tried again at the next opportunity" | No transient class exists. Any server response is final; only a transport failure leaves an envelope queued, and it stops the flush rather than continuing |
| 7 | `docs/events.md` — outcome table | `Rejected` is "a 4xx — it will never work" | `Rejected` is any non-success status including 5xx, 408 and 429. "Will never work" is also the wrong reading: it means Hyperwyc will not try again |
| 8 | `docs/events.md` — sample | The `else` branch handles a transport failure inside an `OnFailed` subscription | Unreachable. `OnFailed` is published only from `DeadLetterAsync`, so `Kind` is always `Rejected`. A transport failure publishes no event at all |
| 9 | `docs/events.md` | "Every event carries a `CorrelationId`" | Null on `OnUpdated` (documented on the type) **and** on the `OnDelivered` published from the online write path, which is constructed with four arguments. A caller who sets `HyperwycRequestOptions.CorrelationId` on a write that goes out online cannot correlate the event. Decide whether that asymmetry is deliberate before rewording |
| 10 | `docs/connectivity.md` ×2 | "silence the startup warning", "not to get past the startup error" | ADR 0007 replaced the throw with one `LogInformation`, whose own comment says it is not a warning that anything is broken |
| 11 | `docs/delivery.md` | "a failure due to a connectivity or **unknown** fault is queued" | `HttpRequestError.Unknown` is explicitly excluded by `NeverReachedTheApi`, along with `InvalidResponse`, `ResponseEnded` and `HttpProtocolError`. Only four errors queue |
| 12 | `docs/events.md` — outcome table | Lists `Headers` as a member of `DeliveryOutcome` | It was removed by the ADR 0004 audit, which names `DeliveryOutcome.Headers` in its list. Same table row as finding 1, so both go in one edit. Found while doing [61](Done/61-xml-docs-contradict-the-code.md) |

## The limitations section understates the limitations

Separate from the list above, and the one most likely to cost a consumer.

`docs/choosing.md` has a **Current limitations** section listing one item, buffered bodies. A
reader evaluating the library reasonably concludes that is the only one. The open backlog says
otherwise, and four of these are consumer-visible and cannot be worked around:

- `Cache-Control` is ignored entirely, including `no-store` ([41](41-honour-cacheability-directives.md))
- the cache grows without bound; nothing evicts ([42](42-cache-eviction.md))
- `Vary` is not honoured, so a content-negotiated endpoint serves the wrong variant, silently
  ([43](43-honour-vary-header.md))
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

## Acceptance Criteria

- [ ] Every row in the findings table is either corrected or, where the code is what should
      change, has its own item.
- [ ] `Current limitations` names every open item a consumer cannot work around, and links to it.
- [ ] The `NetworkOnly` offline-read behaviour reads the same in `delivery.md` and `responses.md`.
- [ ] Anchors resolve.
- [ ] Finding 9 has an answer — deliberate asymmetry, or a gap on the online write path.

## Notes

- **Two of these are behaviour changes that outran their documentation rather than drift.**
  Findings 1, 6 and 7 all come from the same change (any server response is final), and 5 from
  the ADR 0004 audit. Worth remembering that removing a concept from the code does not remove it
  from the pages that explained it — the removal audit collected the orphaned *code* and nothing
  went looking for the orphaned prose.
- Finding 2 is the one with a live failure mode attached and the only one where the code is
  right and two separate documents disagree with it in opposite directions.
- Finding 9 may be a code gap rather than a documentation one. Decide before rewording.
