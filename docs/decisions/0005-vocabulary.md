# 5. Name things for the purpose, not the mechanism

**Status:** Accepted — applied 2026-09-05 as a pure rename, no behaviour change.

## Context

Two families of name were wrong, and both had already misled a design discussion.

**"Sync"** — `ISyncStore`, `SyncEvent`, `SyncOrchestrator`, `SyncOutcome` — names bidirectional
synchronisation with conflict resolution. That is Realm and CommunityToolkit.Datasync: the
category Hyperwyc's own documentation spends paragraphs distancing itself from. A reader meeting
`ISyncStore` reasonably expects the thing we explicitly are not.

**"Cache"** had the opposite problem: it was accurate, and it was the *only* word, so it stretched
over the write path where it does not fit. That produced a real error in reasoning during design
— "you don't serve a POST from cache" — which is true of the word and false of the mechanism.
Hyperwyc absolutely does hold a write locally when it cannot send it. That is the same store,
used in the other direction.

Naming shaped the thinking, not just the reading. That is the reason this was worth doing rather
than tolerating.

## Decision

Settled 2026-09-05. Two rules, and the reasoning is worth keeping because naming shaped design
decisions here more than once.

**"Sync" is gone.** It named bidirectional synchronisation with conflict resolution — Realm,
CommunityToolkit Datasync, the category "What It Doesn't Do" exists to distance Hyperwyc from.
What the write path actually does is queue and deliver, and that vocabulary was already in the
code (`outbox`, `flush`). So: `IHyperwycStore`, `InMemoryStore`, `CabinetStore`,
`OutboxProcessor`, `HyperwycEvent`, `DeliveryOutcome`, `MarkDeliveredAsync`, `OnDelivered`.

**"Cache" stayed, and narrowed.** Every use of it is genuine caching, and `CacheFirst` /
`NetworkFirst` are Workbox's names — vocabulary developers already have. The problem was never
the word; it was that "cache" was the *only* word, so it stretched over the write path where it
does not fit. Once "outbox" and "deliver" cover writes, "cache" retracts to what it describes.

**`CacheStrategy` became `SourcePriority`.** It was the one casualty: `NetworkOnly` governs
writes as well as reads, so a name claiming "cache" over-reached. `NetworkStrategy` would have
been the same mistake mirrored — every candidate that named one side left a third of the values
arguing with it. `SourcePriority` names the *relationship*: which source takes precedence, with
`NetworkOnly` the degenerate case where one has all of it. `RoutePolicyMap.Resolve` became
`PolicyFor` in the same pass, so "resolving a policy" and "resolving a request" stopped sharing
a word.

**`Envelope.IsSynced` was deliberately left.** It is set `true` on cached responses, so it means
"not a pending outbox entry" — renaming it `IsDelivered` would have been actively false rather
than merely vague. That it could not be renamed is the tell that the model is wrong, not the
name; tracked as [issue 55](../../Backlog/Done/55-envelope-kind-discriminator.md).

Run as a pure mechanical pass with no behaviour edits, test count identical either side (29 and
263). Backlog items in `Done/` keep the old vocabulary, since they record what was decided when.

## Consequences

**The public surface changed wholesale**, which was free: nothing was released. It would not be
free again, which is why it happened before the first package rather than after.

**`Envelope.IsSynced` survived, and that is the finding.** It could not be renamed to anything
honest, because it is set on cached responses as well as delivered writes — it means "not a
pending outbox entry", by negation, on a type that is two things at once. A rename with nowhere
to go is evidence about the model rather than the name; tracked as
[issue 55](../../Backlog/Done/55-envelope-kind-discriminator.md).

**Backlog items in `Done/` keep the old vocabulary.** They record what was decided when, and
rewriting them would falsify the record.

## The test this establishes

Before naming a type, ask what it is *for*, not what it *is made of*. A mechanism name is
accurate on the day it is written and becomes a lie as soon as the mechanism serves a second
purpose — and by then it has taught everyone reading it the wrong model.

Two corollaries from this pass:

1. **Borrowed names stay borrowed.** `CacheFirst` and `NetworkFirst` are Workbox's, and
   developers arrive knowing them. Renaming those would have cost more than it bought.
2. **A name covering two sides of a relationship must not pick one.** `CacheStrategy` named one
   side; `NetworkStrategy` would have been the identical mistake mirrored, with a third of the
   values arguing with the name either way.
