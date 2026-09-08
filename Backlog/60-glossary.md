# Issue 60 — Glossary

## Summary

A short page defining the words the documentation uses as though they were already defined:
outbox, envelope, replay, flush, dead-letter, correlation id, synthetic response, stale, TTL.

## Status

⬜ Open. Filed 2026-09-08, from the author's TODO in `docs/offline-writes.md`.

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
| **Envelope** | Two things wearing one type ([55](55-envelope-kind-discriminator.md)), so even the code is unclear |
| **Replay** | Specifically means through the application's pipeline ([ADR 0002](../docs/decisions/0002-replays-traverse-the-pipeline.md)), which is the non-obvious half |
| **Flush** | Three triggers and no scheduler; the word implies more automation than exists |
| **Dead-letter** | Above |
| **Correlation id** | Explicitly *not* an idempotency key ([ADR 0001](../docs/decisions/0001-idempotency-is-not-hyperwycs-remit.md)); not required to be unique; not deduplicated on |
| **Synthetic response** | A response Hyperwyc invented, as against one it remembered. The distinction is the whole of `responses.md` |
| **Stale** | A validity bound, not a refetch trigger ([54](Done/54-ttl-as-a-validity-bound.md)) |

Half of these entries are one line pointing at an ADR, which is the point — the thinking is done,
it is just not reachable from the word.

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
