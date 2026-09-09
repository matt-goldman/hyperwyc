# Issue 60 — Glossary

## Summary

A short page defining the words the documentation uses as though they were already defined:
outbox, queued write, cached response, replay, flush, dead-letter, correlation id, synthetic response, stale, TTL.

## Status

⬜ Open. Filed 2026-09-08, from the author's TODO in `docs/offline-writes.md`.

> **The dead-letter entry dissolved rather than getting written**, which is the outcome this item's own analysis predicted. [66](66-dead-letter-store-fails-the-scope-test.md) removed the concept, and [69](69-expiring-queued-writes.md) may later reclaim the word for what a message bus means by it — *could not be delivered* — at which point the entry gets written for that instead. The rest of the glossary is unaffected and still wanted.

## The term that prompted it

**Dead-lettered.** The word is borrowed from message queues, where it means roughly "we gave up
on this". Here it means almost the opposite: the request reached the API, which was Hyperwyc's
entire job, so from Hyperwyc's side the delivery *succeeded* and what failed is downstream of the
responsibility it took on.

A reader who knows the term from RabbitMQ or Service Bus is therefore *more* likely to be alarmed
by it than one who does not — they will read it as data loss. It appears across four pages with
no definition anywhere.

Two things fix it and both are worth having: a one-sentence definition at first use in
`docs/offline-writes.md`, and this page.

## Why a page as well

Every one of these is load-bearing and none is defined:

| Term | Why it needs saying |
|---|---|
| **Outbox** | Named after the pattern, but Hyperwyc's is a one-attempt-per-flush queue with no scheduler, which is not what most readers picture |
| **Queued write** / **Cached response** | The two kinds of stored record, and the two types that hold them since [55](Done/55-envelope-kind-discriminator.md). "Envelope" was one type doing both and is gone; a glossary written before that would have had to define the ambiguity rather than the terms |
| **Replay** | Specifically means through the application's pipeline ([ADR 0002](../docs/decisions/0002-replays-traverse-the-pipeline.md)), which is the non-obvious half |
| **Flush** | Three triggers and no scheduler; the word implies more automation than exists |
| **Dead-letter** | Above — and see the rename question below, which may remove the need for an entry at all |
| **Correlation id** | Explicitly *not* an idempotency key ([ADR 0001](../docs/decisions/0001-idempotency-is-not-hyperwycs-remit.md)); not required to be unique; not deduplicated on |
| **Synthetic response** | A response Hyperwyc invented, as against one it remembered. The distinction is the whole of `responses.md` |
| **Stale** | A validity bound, not a refetch trigger ([54](Done/54-ttl-as-a-validity-bound.md)) |

Half of these entries are one line pointing at an ADR, which is the point — the thinking is done,
it is just not reachable from the word.

## The dead-letter entry may dissolve rather than get written

Reframed on 2026-09-09, and it changes what this item is for.

`offline-writes.md`'s table used to read *"the server answered, **with anything other than success**"* → dead-lettered. That qualifier invents a category. The page's own lead sentence says the distinction that decides everything is **did the server answer?** — and by that test a `409` and a `201` are the same outcome: the request reached the API, which is the whole of what Hyperwyc promised. What the answer *was* is a conversation between the application and its backend.

So the two terminal states do not differ in whether Hyperwyc succeeded. **They differ in what is kept:**

| | Store | Retained |
|---|---|---|
| `2xx` | `MarkDeliveredAsync` | Nothing — there is nothing you need from it |
| anything else | `MoveToDeadLetterAsync` | The envelope, with status, reason phrase and body, so it can still explain itself after a restart ([40](Done/40-surface-deferred-outcomes.md)) |

That is a real and useful distinction. "Dead-letter" is the wrong name for it: borrowed from message queues, where it means *we gave up on this*, when here it means *delivered, and the answer kept for you*. A reader who knows the term is more likely to be alarmed than one who does not — which is exactly what [ADR 0005](../docs/decisions/0005-vocabulary.md) is about, and the same shape as [55](Done/55-envelope-kind-discriminator.md)'s `IsSynced`.

**Which inverts this item's original purpose.** It was filed to *explain* dead-lettering. If the concept is renamed, there is nothing left to explain — which is the [ADR 0004](../docs/decisions/0004-default-to-removal.md) answer: do not document a bad name, fix it. Decide the rename first; the glossary entry is whatever survives.

What a rename would touch, all public: `IHyperwycStore.MoveToDeadLetterAsync`, `Envelope.IsDeadLettered`, `HyperwycEventType.OnFailed`, `DeliveryOutcomeKind.Rejected`. `OnFailed` is the weakest of them — nothing failed.

It also tidies [24](24-v1-dead-letter-management.md). "Requeue and dismiss" reads as un-failing a failure; under this model, requeuing is the application choosing to make a *new* write from a kept answer, which is a cleaner thing to build and to explain.

The docs have been corrected in the meantime, and `offline-writes.md` now carries a short note saying the API's name means something narrower than the term usually does. That is a holding position, not the fix.

## It also gates a rename

`docs/delivery.md` was `docs/caching.md`, and its title was "Caching, deliver, and route policies", until both were changed on 2026-09-05. The reasoning is worth keeping, because it is [ADR 0005](../docs/decisions/0005-vocabulary.md) applied to a document rather than to a type:

- **"Cache" is a mechanism, not a feature.** It is a real and public one, but naming the page after it names the machinery rather than the purpose — which is the thing 0005 exists to stop.
- **"Delivery" covers both halves.** The page is as much about serving a read from wherever it comes from as it is about the outbox and writes. "Caching" stretches over the write path, which is exactly the failure the backlog's own vocabulary note records: *"'Cache' stayed because every use of it was genuine caching — the problem was that it was the only word, so it got stretched over the write path."*

The title was briefly changed back during [56](Done/56-documentation-restructure.md), resolving a four-names-for-one-document inconsistency toward the leftovers rather than toward the deliberate name. Restored, and recorded here so it is not re-decided a third time by whoever notices the docs and the nav disagreeing.

**Any further rename waits for this item.** Deciding what the page is called before deciding what the words mean is backwards, and a file rename should happen once — the repo is about to be public, so a moved path breaks external links. Whatever the glossary settles should then change the filename and the title together, as the first rename did.

Scope, if it does move: "cach*" appears about 65 times across `docs/` and the README, concentrated in `delivery.md` (19), `responses.md` (9) and `choosing.md` (8). Most of those are genuine caching and should stay. The question the glossary has to answer is which of **store**, **cache** and **delivery** owns which idea, and it is the same question for the code — `IHyperwycStore`, `CachedResponse`, `InvalidateCacheOnWrite` and `MaxCachedResponseBodyBytes` all sit on the boundary.

## Acceptance Criteria

- [ ] Every term above has an entry.
- [ ] `dead-lettered` is defined at first use in `docs/offline-writes.md`, not only in the
      glossary.
- [ ] Entries link to the ADR or item that owns the reasoning rather than restating it.

## Notes

- [ADR 0005](../docs/decisions/0005-vocabulary.md) already did the thinking. A glossary is its
  consumer-facing face, and the ADR is the place to check that an entry says what was actually
  decided.
- Small, and independently useful. Does not need to wait for
  [56](Done/56-documentation-restructure.md), though it belongs to the same pass.
