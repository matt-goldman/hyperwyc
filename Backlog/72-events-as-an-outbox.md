# Issue 72 — Should the Event Stream Have a Delivery Guarantee?

> **NOTE:**
> This will almost certainly get closed unactioned but is recorded as the pattern should be documented following item 66.
> `IObservable<HyperwycEvent>` is already a seam. A consumer who needs durability can put their own durable subscriber behind it today — write to their store on `OnNext`, process from there — and that is the application-owned-store pattern [50](50-resilient-applications-guide.md) describes, applied to notifications instead of records. Between this and simply choosing when to flush in your app lifecycle, making this a Hyperwyc feature is unlikely to be required.
> 
> If the honest answer turns out to be *the seam already supports this, here is a worked example*, that is a success and this item closes with a documentation change. Enabling a choice and making it are different acts.

## Summary

`HyperwycEventStream` is hot and publishes indiscriminately. If nobody is subscribed at the moment an event is raised, it is gone — there is no replay, no buffer, and no record that it happened.

Investigate whether Hyperwyc should offer something stronger: events treated as an outbox in their own right, retained until a consumer has actually taken them. **At-least-once, not exactly-once** — the same guarantee a message bus offers, and the same obligation on the consumer that comes with it.

**This item deliberately does not propose a design.** The question is whether the guarantee is wanted and what shapes are available; picking one before that is answered would be the mistake [ADR 0004](../docs/decisions/0004-default-to-removal.md) exists to prevent.

## Status

💭 Under consideration, unscheduled, **v2.0+ and explicitly not on any critical path**. Filed 2026-09-10.

## Why it is worth capturing now

[ADR 0010](../docs/decisions/0010-delivery-ends-hyperwycs-interest.md) raised the stakes without changing this mechanism. Before it, a delivery outcome was published *and* persisted on the envelope, so a missed event cost you a notification. Now the envelope is discarded on delivery, and **the event is the only report there is**. A missed event is a lost outcome.

Two things currently stand in for a guarantee, and both are mitigations rather than promises:

| | What it does | What it does not do |
|---|---|---|
| `FlushOnStartup` defaults to `false` | Stops the most likely race — a flush completing inside host startup, before a subscriber attaches | Nothing for an event raised while a subscriber is being replaced, or after one has faulted, or during a connectivity-triggered flush the app was not expecting |
| "Subscribe before you flush", in the docs | Tells the consumer where the sharp edge is | Depends on them reading it, and on their composition order actually permitting it |

That is a reasonable place to be for v1. It is not a guarantee, and the documentation does not claim one. The question is whether it should be able to.

## The tension that has to be answered first

**Persisting events looks like the thing [ADR 0010](../docs/decisions/0010-delivery-ends-hyperwycs-interest.md) just removed.** It is not obviously distinct, and any investigation that skips past this has skipped the hard part.

- The case *for* a distinction: the retention 0010 condemned was of an **application-owned artefact** — an ordinary HTTP response, filed on the application's behalf, with no expiry Hyperwyc could reason about. An undelivered notification is different in kind: it is Hyperwyc's own outstanding work, and it has a natural terminus — the consumer takes it, and it goes. That is the same shape as the outbox, which passes the scope test comfortably.
- The case *against*: the event **carries** the response. Retaining events is retaining responses by another route, with the status filter and the eviction policy left implicit rather than chosen. If that is what it amounts to, this is [67](67-configurable-response-retention.md) wearing a different name, and the two items should merge rather than both exist.

Whichever way that lands, it should be argued explicitly and probably in an ADR, because it either draws a line next to 0010's or admits there was never a line there.

## What an investigation would have to establish

- **Is the gap real in practice?** Measure it, the way [66](66-dead-letter-store-fails-the-scope-test.md) measured the startup race rather than reasoning about it. A guarantee built for a problem nobody hits is apparatus.
- **What does "taken" mean?** At-least-once needs someone to say *I have this*. An acknowledgement is a public API and an obligation on the consumer, which is [the scope test's question 2](../docs/decisions/README.md#the-standing-scope-test) — does it require anything of them? A consumer who forgets to acknowledge has built an unbounded store without meaning to.
- **What is the boundary of the guarantee?** Per process? Across a restart? Per subscriber, or one shared cursor for however many there are? These are different features with the same name.
- **What bounds it?** Every retention question in this repo has ended up at eviction — [42](42-cache-eviction.md), [67](67-configurable-response-retention.md), [69](69-expiring-queued-writes.md). This one will too, and an unacknowledged-events store with no bound is the same defect in a new place.
- **What does it cost the common case?** Most consumers subscribe once at composition and never miss anything. A durable write per event is a real cost paid by everyone to fix a problem some have.

## It may be an [ADR 0009](../docs/decisions/0009-provide-the-seam-not-the-alternatives.md) answer rather than a feature

Worth holding open from the start, because it is the cheapest outcome and would be easy to miss once implementation thinking begins.

## Acceptance Criteria

- [ ] The gap is measured rather than asserted: a case where an event is lost that is not developer error.
- [ ] The relationship to [ADR 0010](../docs/decisions/0010-delivery-ends-hyperwycs-interest.md) is settled in writing — either a principled distinction, or an admission that this is [67](67-configurable-response-retention.md).
- [ ] Options are compared without one being assumed, including doing nothing and including reference code over a shipped feature.
- [ ] Whatever is proposed is bounded, and the bound is stated.

## Related

- [65](65-startup-flush-requires-a-host.md) — the trigger timing that makes the current gap reachable.
- [67](67-configurable-response-retention.md) — the other retention question, and possibly the same one.
- [23](done/23-v1-d0agnostics-view.md) — a read path over the outbox; a read path over undelivered events would be its sibling.
- [50](50-resilient-applications-guide.md) — where the do-it-yourself answer would be written up.
