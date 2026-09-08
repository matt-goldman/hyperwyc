# Issue 66 — The Dead-Letter Store Fails the Scope Test

## Summary

Hyperwyc succeeds or fails at **delivery**. A `201` and a `409` are the same event from its side: the request reached the API and the API answered. Yet a non-`2xx` answer is moved to a dead-letter store and kept indefinitely, while a `2xx` is discarded.

That retention is Hyperwyc holding application data on the basis of a distinction it has just decided is not its business. It fails [the scope test](../docs/decisions/README.md#the-standing-scope-test), and [ADR 0004](../docs/decisions/0004-default-to-removal.md) says a decision to decline a responsibility invalidates the apparatus that served it.

## Status

💭 Under consideration. Filed 2026-09-09. **Scope decision — needs an ADR, not just this item**, per the backlog's own convention.

## The argument

### Question 4 draws the line, and it does not fall where the code puts it

> *Does it depend on something only Hyperwyc knows — connectivity, that a request is queued, that a request is a replay, or the contents of the outbox? If no, it belongs elsewhere.*

**The event passes.** Only Hyperwyc knows that this response belongs to a request whose caller was handed a `202` and moved on. Nobody else can join those two facts, which is exactly why [40](Done/40-surface-deferred-outcomes.md) was right to surface it.

**The store fails.** Once the event has been raised, what remains is an ordinary HTTP response. Nothing about *keeping* it depends on anything only Hyperwyc knows. Any application that wants it can keep it, filed under the correlation id it chose itself — which is precisely the application-owned-store pattern [50](50-resilient-applications-guide.md) and [Design](../docs/design.md) already tell people to build.

So the Hyperwyc-shaped part is the notification. The filing cabinet is not.

### The asymmetry is the tell

`docs/events.md`, before this was noticed:

> Failure detail is persisted on the envelope so a dead-lettered write can still explain itself after a restart, but **success detail is not** — a delivered envelope leaves the outbox.

If both are deliveries, keeping one and discarding the other is arbitrary. The asymmetry is only coherent under the belief that a non-`2xx` is a *Hyperwyc* failure, which is the belief being rejected. A design that treats two members of one category differently is usually carrying a distinction it has not admitted to.

### It also inherits every problem of an unbounded store

Nothing evicts it — [42](42-cache-eviction.md)'s shape, arriving by a third route. On a long-lived app it accumulates HTTP responses that Hyperwyc cannot interpret, cannot act on, and cannot expire, because only the application knows when one has been dealt with.

## The honest tension

Item 40's justification for persisting was real and should not be waved away:

> a background flush can complete while the application is not running, so an outcome delivered only as an event is an outcome nobody hears about.

That is true. If Hyperwyc keeps nothing, an app that was dead during the flush learns only that its own record is still unsynced.

Three things reduce it, though none dissolves it entirely:

1. **That is already the situation for a `2xx`**, and nobody has called it a defect.
2. **The application is told to own its record anyway.** Its row is still marked unsynced, and reconciling is a read it was going to do.
3. **Re-reading the resource is already the documented answer** for the success case.

What genuinely does not survive is a `409` whose *body* explained why — that is unrecoverable by re-reading, and it is the strongest single argument for keeping anything. Whether that justifies a store, or belongs to the application that chose to care, is the question the ADR has to answer.

## The two options

Settled: **the dead-letter concept is wrong and goes.** What replaces it is one of exactly two things, and they are mutually exclusive on the storage axis:

**A — rename to *delivered*, and retain irrespective of outcome.** Hyperwyc becomes a queryable record of what it did. Consistent, and it makes the `409` body recoverable.

**B — retain nothing once delivered.** Hyperwyc is a conduit; its store holds only outstanding work.

### Measured: an event can be missed without it being developer error

The strongest argument for A is "a consumer might not see the event". That was assumed to require Hyperwyc running while the app is not, which it cannot do — every flush trigger is in-process. **It does not require that.**

```
queued: 202
events seen by a subscriber that attached after startup: NONE
still in the outbox: 0  → the write WAS delivered
```

`FlushOnStartup` runs inside host startup. Anything subscribing later — a page loading, a view model constructing, a lazily-resolved service — misses what it delivered, because `HyperwycEventStream` is hot with no replay. That is the ordinary application shape, not a misuse.

**But it has a cheaper fix than a store**: `FlushOnStartup = false` and an explicit `FlushAsync()` once subscribed, which works today. Related to [65](65-startup-flush-requires-a-host.md), which is the same trigger seen from the other side.

Do not decide the storage question on the strength of a problem that costs one line to remove.

### Three things that push toward B

1. **The motivating scenario presupposes its own answer.** A list of inspection reports showing delivered-versus-pending requires the app to have its own store — Hyperwyc cannot render a list, it is not a local database. So the case where Hyperwyc's record looks necessary is the case where the application already holds the fact, and is already updating it from the event.
2. **"Query on demand" survives either option.** A consumer should be able to ask about a request that is still *outstanding* — that is [23](23-v1-diagnostics-view.md), and it passes Q4, which names "the contents of the outbox" explicitly. B removes history from the queryable set, not the ability to query.
3. **Bounded versus unbounded.** B's store is bounded by work in progress and empties by construction. A's grows with usage and needs a retention policy Hyperwyc has no basis to choose — and unlike the response cache, a delivery record cannot be evicted safely, because eviction loses the only copy.

### The argument for A that has to be answered

Not "you should have architected differently" — declining to hold the data is the same boundary as declining conflict resolution, and the alternative is not *you cannot know*, it is *you know when it happens and keep what you need*.

What genuinely does not survive B is **the body of a non-`2xx` answer**. A `409` explaining *why* cannot be recovered by re-reading the resource, and no amount of application-side discipline reconstructs it after the fact. That is the one real cost, and the ADR has to either accept it or find the smallest thing that covers it.

## Settled while reasoning it through

- **The current path is B**: remove dead-lettering, keep only what still needs sending. Retention comes back later, deliberately, as [67](67-configurable-response-retention.md) — filed rather than remembered.
- **Discard the request once delivered**, not only the outcome. The request body and its headers are the largest *and* most sensitive things in the store ([30](30-sensitive-header-exclusion.md)); keeping them past delivery extends that exposure for nothing.
- **Absence is not an outcome, and that is B's real cost.** "No longer in the outbox" means delivered, and does not distinguish accepted from rejected. An application that missed the event and infers success from absence is silently wrong. So under B the event is not merely the best report, it is the *only* one.
- **Which makes 66 conditional on [65](65-startup-flush-requires-a-host.md).** Shipping "the event is your only chance" while the startup flush can fire before a subscriber attaches would be shipping a contract the library breaks by default. Fix the race, or move the trigger, first or together — not after.

## What comes out if it goes

- `IHyperwycStore.MoveToDeadLetterAsync`, `Envelope.IsDeadLettered`, and the dead-letter partition
- `HyperwycEventType.OnFailed` — nothing failed; a delivery raises one event whatever the status
- `DeliveryOutcomeKind.Succeeded` / `Rejected` collapse into one, leaving delivery versus transport failure
- [24](24-v1-dead-letter-management.md) dissolves. "Requeue and dismiss" is un-failing a failure; under this model requeuing is the application making a *new* write, which it can already do
- [23](23-v1-diagnostics-view.md) narrows to the outbox, which is genuinely Hyperwyc's
- [60](60-glossary.md)'s dead-letter entry disappears rather than being written

`DeliveryOutcome` itself stays. It is what the event carries, and the event is the part that passes Q4.

## The word may come back, for what it actually means

Removing this does not banish "dead-letter". In a message bus the term means **could not be delivered**, and it was being applied here to requests that *had* been — the misuse was the direction, not the borrowing.

Once that is gone, the question it was standing in front of becomes askable: should a write Hyperwyc has failed to deliver for long enough simply expire? That is dead-lettering, correctly used, and it comes out the *other* side of the scope test — the age of something in the outbox is precisely what only Hyperwyc knows. Filed as [69](69-expiring-queued-writes.md).

## Acceptance Criteria

- [ ] An ADR records the distinction — delivery is Hyperwyc's success axis, HTTP's is not — and what it condemns.
- [ ] The `409`-body question has an answer, not an omission.
- [ ] No public member describes a delivered request as failed.
- [ ] `docs/design.md` and `docs/offline-writes.md` stop carrying the "under review" caveat they carry now.

## Notes

- **Found by pulling on a word.** The chain was: "dead-lettered" needs explaining ([60](60-glossary.md)) → the thing needing explanation is a bad name → the name is wrong because the table's qualifier invents a category → removing the qualifier shows the two states differ only in what is retained → the retention is not Hyperwyc's to hold. Each step was small; the last one is a public API change. Worth remembering that a glossary entry nobody could write cleanly was the symptom.
- **This is the second time this session that a naming problem turned out to be a model problem.** [55](55-envelope-kind-discriminator.md) is the other: `IsSynced` could not be renamed honestly because the type was two things. Same tell, same conclusion — when no name fits, the thing is wrong, not the word.
- The docs already state the principle. `design.md` leads with it and flags the store as the one place the code has not caught up; `offline-writes.md` says the retention is under review. Both caveats come out when this does.
