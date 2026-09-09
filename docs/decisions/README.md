# Architecture Decision Records

Decisions that shaped Hyperwyc, and the reasoning behind them — kept so that a future decision of
the same shape can be answered consistently, rather than re-argued from scratch or quietly
reversed.

| # | Decision | Status |
|---|---|---|
| [0001](0001-idempotency-is-not-hyperwycs-remit.md) | Idempotency is not Hyperwyc's remit | Accepted |
| [0002](0002-replays-traverse-the-pipeline.md) | Replays traverse the application's pipeline | Accepted |
| [0003](0003-default-what-you-can-decide-correctly.md) | Default what you can decide correctly; require what you cannot | Accepted |
| [0004](0004-default-to-removal.md) | Default to removal | Accepted |
| [0005](0005-vocabulary.md) | Name things for the purpose, not the mechanism | Accepted |
| [0006](0006-a-shipped-implementation-is-not-a-default.md) | A shipped implementation is not a default | Superseded by 0007 |
| [0007](0007-connectivity-cannot-cost-correctness.md) | Connectivity is an optimisation, not a correctness input | Accepted |
| [0008](0008-shrink-what-you-store.md) | Shrink what you store before reshaping where you store it | Accepted |
| [0009](0009-provide-the-seam-not-the-alternatives.md) | Provide the seam, not the alternatives | Proposed |

## What belongs here

An ADR when a decision **constrains future decisions**: something about Hyperwyc's scope, its
responsibilities, or the boundary between it and the applications using it. Especially a decision
to *not* build something, or to remove something already built, since those are the ones most
easily undone by someone who only sees the gap and not the reason for it.

Not everything needs one. A bug fix does not. Nor does a choice with an obvious right answer and
no lasting implications.

## How this relates to the other documents

| Document | Question it answers |
|---|---|
| [README](../../README.md) | What is it? |
| [docs](../README.md) | How do I use it? |
| [Backlog](../../Backlog/README.md) | What is the work, what state is each piece in, and what is coming? |
| **decisions** | **Why is it this way, and what does that imply for what comes next?** |

A backlog item records what was done and how. An ADR records *why*, and what the decision means
for the next one — which is why the two are separate even when they cover the same change. The
backlog entry for a decision goes to `Done/` and stops being read; the ADR is meant to be read
again.

## Conventions

- Numbered sequentially, four digits, never reused.
- Status is `Proposed`, `Accepted`, `Superseded by NNNN`, or `Deprecated`. A superseded ADR stays
  where it is with its status updated; the reasoning that turned out to be wrong is often the
  most useful part.
- Link to the backlog item that implemented it, so the *what* is one hop away.
- **An accepted ADR is immutable.** Its body is a record of what was decided and why, at the time
  it was decided — editing it destroys the thing the record exists for, and quietly makes past
  reasoning look better informed than it was. Anything to add or clarify goes in a numbered,
  dated **addendum** appended below the body, which says plainly that it does not alter the
  decision. Anything that changes the decision is a new ADR that supersedes this one.

  Two things are not edits and are fine: repairing a link whose target moved, and correcting a
  statement of fact that was wrong when written — the second should say so in an addendum if
  anyone might have relied on it.

## Reference models

Hyperwyc is positioned as "a Service Worker for .NET", so **Service Workers and
[Workbox](https://developer.chrome.com/docs/workbox) are the reference model** — the place to
look before inventing a mechanism, and the vocabulary to borrow when one already has a name.

Some of that is deliberate borrowing and some is convergence, both worth knowing about:

| Hyperwyc | Web equivalent |
|---|---|
| `HyperwycHandler` in the `HttpClient` pipeline | The `fetch` event and `respondWith` |
| `SourcePriority` — cache-first, network-first, network-only | Workbox's strategies |
| `RoutePolicy.Ttl`, `MaxCachedResponseBodyBytes` | Workbox's `ExpirationPlugin`, `CacheableResponsePlugin` |
| Outbox, and replay on connectivity change | Background Sync, and Workbox's `BackgroundSyncPlugin` queue |
| `IObservable<HyperwycEvent>` | `clients.postMessage`, and `BroadcastUpdatePlugin` for cache updates |
| Prefetch on boot (v2.0) | Precaching |
| Scheduled background replay (v2.0) | Periodic Background Sync |

Two of those were arrived at independently and only recognised afterwards, which is reassuring
rather than embarrassing: the retry model in
[issue 38](../../Backlog/Done/38-retry-classification.md) is essentially Background Sync's, and
the conclusion in [issue 34](../../Backlog/Done/34-app-lifecycle-integration.md) that durability
comes from persistence rather than shutdown hooks is exactly why Background Sync is
browser-managed rather than page-managed.

### Where the analogy does not carry

- **Opaque responses, CORS, navigation preload, `skipWaiting`/`clients.claim`, Push.** Browser
  concerns with no counterpart in an in-process HTTP handler.
- **Range and partial responses.** Workbox has `createPartialResponse`; treated as out of scope
  alongside streaming bodies.
- **Storage quota.** This one inverts. A browser hands an origin a quota and evicts under
  pressure, so a Service Worker cooperates with a system that is already managing growth.
  Hyperwyc has no such backstop, which is why bounding the cache
  ([issue 42](../../Backlog/42-cache-eviction.md)) is work we have to do rather than behaviour we
  inherit.

### The trap: borrowing a stance without its precondition

Worth stating on its own, because it was found the hard way.

A Service Worker's cache ignores `Cache-Control` entirely. That is defensible **because a
Service Worker caches only what you opted in, route by route** — the developer named the
resource, so the server's opinion is secondary to an explicit local decision.

Hyperwyc caches every GET by default. It had adopted the same stance without the opt-in that
justified it, and so was storing responses whose servers had said `no-store`
([issue 41](../../Backlog/41-honour-cacheability-directives.md)).

When borrowing from the reference model, borrow the *reasoning*, not just the behaviour, and
check that the conditions making it safe there also hold here.

## The standing scope test

[ADR 0001](0001-idempotency-is-not-hyperwycs-remit.md) derives a test for whether a capability
belongs in Hyperwyc at all. Reproduced here because it is meant to be used, not filed:

1. Would this problem exist without Hyperwyc? If yes, it has existing owners.
2. Does it require anything of the consumer's API? If yes, it is an imposition.
3. Can the application already do it at the call site or in its own handler? If yes, our job is
   to not interfere.
4. Does it depend on something only Hyperwyc knows — connectivity, that a request is queued, that
   a request is a replay, or the contents of the outbox? If no, it belongs elsewhere.

## The standing defaults test

[ADR 0003](0003-default-what-you-can-decide-correctly.md) covers the capabilities that pass the
scope test: given that Hyperwyc should do this, should it have a default?

0. Is this input load-bearing at all, or does something downstream already know better? If the
   system finds out for itself one layer down, the choice is about efficiency and the rest of
   this test does not apply — see [ADR 0007](0007-connectivity-cannot-cost-correctness.md).
1. Can Hyperwyc choose correctly from what it knows? Platform, API and domain knowledge belong
   to the application.
2. If the default is wrong, does the consumer find out? Wrong-and-loud is fine; wrong-and-silent
   ships.
3. Could a wrong default quietly produce the failure the library exists to prevent? Then never.
4. If it must be required, is the fix short and obvious? Ship something to point at, and make
   sure requiring a decision has not quietly required an *ordering* as well.
5. Would this default be equally right everywhere Hyperwyc runs? An implementation good on one
   host and unreliable on another is worth naming in the docs; whether it must be *chosen*
   depends on question 0 — see [ADR 0006](0006-a-shipped-implementation-is-not-a-default.md) and
   [ADR 0007](0007-connectivity-cannot-cost-correctness.md).

## The standing removal test

[ADR 0004](0004-default-to-removal.md) is the one to reach for first, because it changes where an
assessment starts rather than how it concludes. **Begin from the premise that the problem is
something we added and did not need.**

1. What can come out? Ask before asking what goes in.
2. Does a decision already taken orphan this code? Declining a responsibility invalidates the
   apparatus that served it — go and collect it.
3. Is this removing complexity or relocating it to the consumer? Both can be right; only one is a
   reduction.
4. Is this machinery around the promise, or the promise? The second is not negotiable.

A default, not a conviction. Cheap when wrong, valuable when right.
