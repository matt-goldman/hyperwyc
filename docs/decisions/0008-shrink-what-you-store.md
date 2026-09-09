# ADR 0008 — Shrink what you store before reshaping where you store it

**Status:** Accepted

**Date:** 2026-09-09

## Context

[Issue 52](../../Backlog/Done/52-store-rewrites-whole-set-per-write.md) found that every single-record change through Cabinet's `RecordSet<Envelope>` rewrites the entire record set: `AddAsync`, `UpdateAsync` and `RemoveAsync` all end in `SaveAllAsync`, which serialises, encrypts and rewrites one document containing every envelope. Filling a cache of n responses therefore costs O(n²).

The item proposed three options and named the first as the right answer: **per-document storage in Cabinet** — one file per envelope, or an append-and-compact log — with batching in Hyperwyc as a fallback and cache eviction ([42](../../Backlog/42-cache-eviction.md)) as a partial mitigation happening anyway.

Option 1 is wrong, and Cabinet already has the measurement that says so. From Cabinet's own [performance principles](https://github.com/matt-goldman/Cabinet/blob/main/_docs/performance-principles.md), writing 5,000 records:

| Strategy              | Files | Total      |
| --------------------- | ----- | ---------- |
| One file per record   | 5,000 | ~10,000 ms |
| Single aggregate file | 1     | ~20 ms     |

Aggregate-per-write is not an oversight in `RecordSet`; it is the measured design, and file count is the thing Cabinet is built to minimise. Asking Cabinet to store one file per envelope is asking it to be 500× slower at the case it was designed around, on behalf of one consumer.

The diagnosis in 52 still stands. The O(n²) is arithmetic, not opinion. But the term that dominates it is not the record count — it is the **bytes each record contributes to the rewritten document**, and Hyperwyc's envelopes are large for exactly one reason: request and response bodies are `byte[]`, which System.Text.Json writes as base64 inside the record. A 512 KB cached body ([`MaxCachedResponseBodyBytes`](../storage.md)) is ~683 KB of base64 in a document that is rewritten on every subsequent cache write.

Cabinet 2.0 can now read an attachment back, which is the thing [issue 25](../../Backlog/Done/25-binary-request-response-bodies.md) said to wait for. Bodies can move out of the record and into per-record encrypted blobs that are written once and never re-serialised.

## Decision

**When a dependency is expensive for Hyperwyc, change what Hyperwyc hands it before proposing to change the dependency.**

Concretely, for 52: bodies move to Cabinet attachments ([70](../../Backlog/Done/70-move-bodies-to-cabinet-attachments.md)) and the cache is bounded ([42](../../Backlog/42-cache-eviction.md)). Cabinet's write path is not asked to change. Issue 52 closes as superseded by those two rather than as fixed.

The general form, which is the part meant to outlast this instance:

- A dependency's design is not a defect because it is inconvenient for one call site. Look for its reasoning and its measurements first — Cabinet's were published, and contradicted the fix we had written down.
- Where a cost is a function of what we hand over, that is ours to reduce. Where it is a function of how the dependency works, it is theirs, and worth raising **on their terms** rather than ours.
- Raising it upstream anyway remains legitimate when the dependency is wrong on its own terms. Both attachment defects found under [issue 25](../../Backlog/Done/25-binary-request-response-bodies.md) — no read path, and `FileAttachment` throwing on serialisation — were of that kind, were raised, and are fixed in Cabinet 2.0. That is the distinction: a bug in the dependency is reportable; a trade-off we happen to be on the wrong side of is ours to design around.

## Consequences

**52 does not get "fixed", and the number it was chasing should still be measured.** Its acceptance criterion asking for a benchmark showing single-record write cost does not grow with store size stays worth having, moved to [70](../../Backlog/Done/70-move-bodies-to-cabinet-attachments.md), because "we made the document smaller" is a prediction until something measures it. The estimate to beat: with bodies inline, a store holding a hundred cached 500 KB responses rewrites tens of megabytes per cache write; with bodies out, the same store's document is under a megabyte.

**Hyperwyc takes on attachment lifecycle it did not have.** `RecordSet.RemoveAsync` deletes a record's attachments, but Hyperwyc's cache invalidation nulls `Response` and updates rather than removing, so it must delete the blob explicitly or orphan it. `CabinetStore.ResetAsync`, which today deletes only top-level files under `attachments/`, must delete the directory tree. Both are in [70](../../Backlog/Done/70-move-bodies-to-cabinet-attachments.md).

**This does not license staying on a bad dependency.** It says the first move is to fit it, not that there is no second move. If fitting it costs correctness rather than convenience, that is a different decision and gets its own ADR.

**It has a mirror in [the standing scope test](README.md#the-standing-scope-test).** That test points upward — is this Hyperwyc's problem or the consumer's? This one points downward — is this Hyperwyc's problem or its dependency's? The answers rhyme: the party who owns the shape owns the cost of the shape. Bodies are Hyperwyc's shape.

## Related

- [Issue 52](../../Backlog/Done/52-store-rewrites-whole-set-per-write.md) — the item this closes.
- [Issue 70](../../Backlog/Done/70-move-bodies-to-cabinet-attachments.md) — the work that replaces it.
- [Issue 42](../../Backlog/42-cache-eviction.md) — bounds the record count, independently.
- [Issue 25](../../Backlog/Done/25-binary-request-response-bodies.md) — where attachments were first considered and deferred, and where the two upstream bugs were found.
- [ADR 0004](0004-default-to-removal.md) — the same instinct applied to features rather than dependencies.
