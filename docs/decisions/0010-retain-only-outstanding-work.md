# ADR 0010 — Retain only outstanding work

**Status:** Proposed

**Date:** 2026-09-09

## Context

[ADR 0001](0001-idempotency-is-not-hyperwycs-remit.md) draws Hyperwyc's boundary at delivery, and [design.md](../design.md#delivery-is-what-hyperwyc-succeeds-or-fails-at) states it plainly: a `201` and a `409` are the same event from here — the request reached the API and the API answered. What the answer *means* is a conversation between the application and its backend, and Hyperwyc has no standing in it.

The store did not agree. A non-`2xx` answer moved the envelope to a dead-letter partition and kept it — request, headers, body and response — indefinitely. A `2xx` left the outbox and was discarded. [Issue 66](../../Backlog/66-dead-letter-store-fails-the-scope-test.md) put that asymmetry against [the standing scope test](README.md#the-standing-scope-test) and it does not survive question 4.

**The event passes.** Only Hyperwyc knows that this response belongs to a request whose caller was handed a `202` and moved on. Nobody else can join those two facts, which is why [issue 40](../../Backlog/Done/40-surface-deferred-outcomes.md) was right to surface it.

**The store fails.** Once the event has been raised, what remains is an ordinary HTTP response. Nothing about *keeping* it depends on anything only Hyperwyc knows. Any application that wants it can keep it, filed under the correlation id it chose itself — which is precisely the application-owned-store pattern [design.md](../design.md) and [issue 50](../../Backlog/50-resilient-applications-guide.md) already tell people to build.

So the Hyperwyc-shaped part is the notification. The filing cabinet is not.

Two further things condemn the retention on their own terms:

- **It was unbounded.** Nothing evicted the dead-letter partition and nothing could: only the application knows when a rejection has been dealt with. On a long-lived app it accumulated HTTP responses Hyperwyc cannot interpret, act on, or expire — [issue 42](../../Backlog/42-cache-eviction.md)'s problem arriving by a third route.
- **The name was borrowed wrongly.** In a message bus, dead-lettering means *could not be delivered*. It was being applied here to requests that **had** been. The misuse was the direction, not the borrowing.

## Decision

**Hyperwyc's store holds outstanding work and nothing else. A delivered write is discarded — the request as well as the outcome — whatever the server said about it.**

Delivery is one state, not two. The only distinction the store makes is the one that determines whether there is still work to do:

|                                           | What Hyperwyc does                                                             |
| ----------------------------------------- | ------------------------------------------------------------------------------ |
| The server answered, with anything at all | The event is published and the envelope is removed. Nothing is retained        |
| No response arrived                       | The outcome is recorded on the envelope, which stays queued for the next flush |

Concretely, and this is the part that is a public API change:

- `IHyperwycStore.MoveToDeadLetterAsync` and `Envelope.IsDeadLettered` are gone, and with them the dead-letter partition.
- `IHyperwycStore.MarkDeliveredAsync` becomes `RemoveDeliveredAsync`, because it no longer marks anything. An implementer who flags rather than removes is now implementing the wrong contract, and the name has to say so.
- `HyperwycEventType.OnFailed` is gone. Nothing failed; a delivery raises `OnDelivered` whatever the status, carrying the outcome.
- `DeliveryOutcomeKind.Succeeded` and `Rejected` collapse into `Delivered`, leaving delivery versus `TransportFailure` — which is the distinction that was doing real work all along.
- `DeliveryOutcome` itself stays. It is what the event carries, and the event is the part that passes question 4.

**The request is discarded too, not only the outcome.** The request body and its headers are the largest *and* most sensitive things in the store ([issue 30](../../Backlog/30-sensitive-header-exclusion.md)); keeping them past delivery extends that exposure for nothing.

### The `409` body: the cost is accepted, not overlooked

The strongest argument against this is real and is not being waved away. A rejection often carries the only explanation of itself — a `ProblemDetails`, a validation payload, an account of what conflicted — and that cannot be recovered by re-reading the resource. Under this decision, an application that misses the event loses it.

That cost is accepted for now, deliberately, and filed as [issue 67](../../Backlog/67-configurable-response-retention.md) rather than remembered. Retention comes back, if it comes back, as something a consumer opts into and supplies the judgement for, with the eviction policy that a permanent store needs. It does not come back as a default, because a default would be Hyperwyc having an opinion about what a status code means, which is the whole of what this ADR declines.

The point of removing it is to find out whether anyone needs it back. If nobody does, 67 closes unbuilt, and that is a success.

### The consequence that had to be paid for first

Under this decision **the event is not merely the best report of an outcome, it is the only one.** "No longer in the outbox" means delivered and does not distinguish accepted from rejected, so an application that missed the event and infers success from absence is silently wrong.

That makes the startup flush load-bearing in a way it was not before. `FlushOnStartup` ran inside host startup, so anything subscribing later — a page loading, a view model constructing, a lazily-resolved service — missed whatever it delivered, `HyperwycEventStream` being hot with no replay. Shipping "the event is your only chance" alongside a default that races the subscriber would be shipping a contract the library breaks out of the box.

**So `FlushOnStartup` now defaults to `false`.** Delivery at startup becomes explicit: subscribe, then call `FlushAsync()`. Against [the standing defaults test](README.md#the-standing-defaults-test):

- **Can Hyperwyc choose correctly from what it knows?** No. Whether a subscriber exists yet is a fact about the application's composition, and only the application knows when it is ready to hear.
- **If the default is wrong, does the consumer find out?** With `true`, no — that is exactly the failure: the outcome is published to nobody and the write is gone from the outbox, which looks identical to a healthy delivery. With `false`, the outbox simply does not drain until something triggers it, which is visible and recoverable. Wrong-and-loud over wrong-and-silent.
- **Is the fix short and obvious?** One line, at a point in startup the application already controls.

This also removes the sharper edge of [issue 65](../../Backlog/65-startup-flush-requires-a-host.md), where the trigger silently did nothing in an application built on a bare `ServiceCollection`. It does not close 65: what remains there is documentation and the unanswered MAUI question, and both still want doing.

### Cache invalidation stays gated on a success status

One place still reads the status code, and it is worth being explicit that this is not the rejected distinction sneaking back in.

`RoutePolicy.InvalidateCacheOnWrite` drops cached reads under a prefix when a write to it is delivered, and it does so only for a `2xx`. That is a **cache-freshness judgement about Hyperwyc's own data**, not a judgement about whether Hyperwyc succeeded, and it matches what HTTP itself specifies: RFC 9111 §4.4 invalidates on a *non-error* response to an unsafe method. Invalidating on a `422` would throw away cached reads that are still perfectly good, and cost an offline read for nothing.

Reading a status code to decide what to do with the cache is in scope. Reading one to decide whether the application's data is worth keeping is not. The line is who owns the data.

## Consequences

**Two backlog items dissolve or narrow, and one is reclaimed.**

- [Issue 24](../../Backlog/24-v1-dead-letter-management.md) is closed unbuilt. "Requeue and dismiss" is un-failing a failure; under this model requeuing is the application making a new write, which it can already do at the call site.
- [Issue 23](../../Backlog/23-v1-diagnostics-view.md) narrows to the outbox, which is genuinely ours — and stays worth building, because a transport failure publishes no event and the outbox is the only place it can be observed.
- [Issue 69](../../Backlog/69-expiring-queued-writes.md) gets the word back for what it means. Should a write Hyperwyc has failed to deliver *for long enough* expire? That is dead-lettering, correctly used, and it comes out the **other** side of the scope test: the age of something in the outbox is precisely what only Hyperwyc knows.

**`Envelope.IsSynced` is left with one job.** It marked cache entries, marked delivered writes, and paired with `IsDeadLettered` to define the outbox. Delivery is now a delete and the dead-letter flag is gone, so what remains is a flag whose entire meaning is *this is a cache entry, not an outbox entry* — which is [issue 55](../../Backlog/55-envelope-kind-discriminator.md)'s thesis with the camouflage removed. It also makes 55's two-types option the cheaper one: four of the six remaining store methods are already kind-specific.

**`Envelope.LastOutcome` now only ever holds a transport failure.** Nothing else survives long enough to be written. It is the record behind [issue 23](../../Backlog/23-v1-diagnostics-view.md), and the reason an outbox that is not draining can still explain itself.

**A store written by an earlier preview will re-deliver its dead letters.** `IsDeadLettered` no longer exists, so a persisted envelope carrying it deserialises without the flag, and `!IsSynced` puts it back in the outbox. `DeliveryOutcomeKind`'s numeric values shift for the same reason. Accepted rather than mitigated: Hyperwyc is at `0.1.0-preview.1`, and a migration path for a shape that no released version has is apparatus for a problem nobody has. Anyone carrying a preview store across this change resets it.

**Removing this does not license removing the event.** [Question 4 of the removal test](README.md#the-standing-removal-test) asks whether a thing is machinery around the promise or the promise itself. The notification is the promise: it is the only way an application learns what happened to a write whose caller was handed a `202` and walked away. What came out is the filing cabinet built around it.

## Related

- [Issue 66](../../Backlog/66-dead-letter-store-fails-the-scope-test.md) — the item this decides, and the full argument including the case for the option not taken.
- [ADR 0001](0001-idempotency-is-not-hyperwycs-remit.md) — where the delivery boundary and the scope test come from.
- [ADR 0004](0004-default-to-removal.md) — the instinct that found this: a decision to decline a responsibility invalidates the apparatus that served it, so go and collect it.
- [ADR 0003](0003-default-what-you-can-decide-correctly.md) — the test applied to `FlushOnStartup`.
- [ADR 0005](0005-vocabulary.md) — why `MarkDeliveredAsync` could not keep its name once it stopped marking.
- [Issue 67](../../Backlog/67-configurable-response-retention.md) — the deliberate, later answer to the one real cost.
- [Issue 65](../../Backlog/65-startup-flush-requires-a-host.md) — the startup trigger seen from the other side.
- [Issue 55](../../Backlog/55-envelope-kind-discriminator.md) — what is left of `IsSynced` afterwards.
