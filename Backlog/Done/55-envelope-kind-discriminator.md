# Issue 55 — `Envelope.IsSynced` Distinguishes Two Kinds of Envelope by Negation

## Summary

`Envelope` is two things wearing one type: a cached response, and a queued write. They are told
apart by `IsSynced`, which is set `true` on a cached response that was never "synced" anywhere.
The flag does not mean what it says, and the vocabulary pass could not rename it because there is
no honest name for what it currently does.

## Status

✅ **Done, 2026-09-10.** Option 2 — two types — as the argument below settles it once [66](../66-dead-letter-store-fails-the-scope-test.md) has landed. 343 tests pass, the same count as before — the flag-negation tests that went were replaced by tests that the two partitions stay separate.

### What landed

`Envelope` is gone. In its place:

| Type | Keyed on | Carries |
|---|---|---|
| `QueuedWrite` | a generated `Id` | the captured request — `Method`, `RequestHeaders`, `RequestBody`, `ClientName`, `CorrelationId`, `CreatedUtc` — and `LastOutcome` |
| `CachedResponse` | its `Url` | the stored response — `StatusCode`, `Headers`, `Body`, `CachedAt` |

`CachedResponse` was already the name of the nested response payload, so this is a promotion rather than a new type: it gained the `Url` that identifies it and lost its container. `CacheEntry`/`OutboxEntry` were the names this item proposed, and both were rejected on ADR 0005's own test — "entry" names the mechanism (a record in a store), where "cached response" and "queued write" are what the documentation has called the two kinds all along.

**`IHyperwycStore` is seven methods, in three groups.** Three for the cache, three for the outbox, one for both:

| | |
|---|---|
| Cache | `GetCachedResponseAsync`, `PutCachedResponseAsync`, `InvalidateCacheForPrefixAsync` |
| Outbox | `GetPendingOutboxAsync`, `UpsertQueuedWriteAsync`, `RemoveDeliveredAsync` |
| Both | `ResetAsync` |

`UpsertAsync` split in two, which the item predicted and which turned out to be the cheap half. `GetCachedResponseAsync` now returns a `CachedResponse` rather than an envelope a caller had to reach through, so `BuildResponseFromEnvelope` lost its `envelope.Response!` and `IsStale` lost the null check that was really a kind check.

**Both stores hold the kinds separately.** `InMemoryStore` keeps two dictionaries — one keyed by URL, one by id. `CabinetStore` keeps two `RecordSet`s over the same `FileOfflineStore`, so they are separate documents with separate id spaces and separately namespaced attachments. That also means a burst of cache writes no longer rewrites the outbox document, which matters because `RecordSet` rewrites the whole set on every single-record change (issue 52).

**The `cache:` id prefix is gone**, and so is the reason for it. A cache entry's identity is now the URL it caches, stated as the key rather than encoded in a synthetic id — the same property, without the convention a store implementer had to be told about.

**Two kind-checks-written-as-field-checks are gone.** `.Where(e => !e.IsSynced)` and [68](68-cache-invalidation-leaves-tombstones.md)'s `Response is not null` filter both existed to answer "is this the other kind?", and neither has anything to check any more.

**A cached response no longer stores the request.** `Envelope.ForCachedResponse` went through `ForRequest`, so every cache entry carried the request's method, headers, body, client name and correlation id — none of which anything read. The headers are the notable ones: a cached `GET` was holding whatever the caller sent, `Authorization` included, for as long as the entry lived. That is ADR 0008's rule applied by consequence rather than by intent, and it narrows but does not close [30](../30-sensitive-header-exclusion.md), which is about the queued writes that legitimately keep their headers.

**No ADR.** This decides nothing that constrains a future decision: it is the follow-through on [ADR 0005](../../docs/decisions/0005-vocabulary.md)'s own finding, which already said the model was wrong and named this item as the place to fix it. The reasoning that would go in an ADR is already there.

### Not done here

**No migration for a store written by an earlier preview.** `Envelope.dat` is not read by anything now, so a carried-over store comes back empty rather than wrong — the outbox looks drained and the cache looks cold. Same disposition as [66](../66-dead-letter-store-fails-the-scope-test.md) took, for the same reason: Hyperwyc is at `0.1.0-preview.1` and a migration for a shape no released version has is apparatus for a problem nobody has. Anyone carrying a preview store across this resets it. **This is worse than 66's version of the same problem** — that one re-delivered dead letters, this one silently drops an outbox — so it is worth being sure the preview really has no users before shipping.

### Original problem statement follows.

## What it does today

```csharp
// Envelope.ForCachedResponse
envelope.IsSynced = true;
```

```csharp
// InMemoryStore.GetPendingOutboxAsync
.Where(e => !e.IsSynced && !e.IsDeadLettered)
```

So `IsSynced` means *"not a pending outbox entry"*, and the outbox is defined by negation. A
cached `GET` response is marked synced because it is not a queued write, which is true and says
nothing about synchronising.

The rename pass renamed `MarkSyncedAsync` to `MarkDeliveredAsync` and `OnSynced` to `OnDelivered`
— both are genuinely about delivery — but deliberately left `IsSynced` alone. Renaming it
`IsDelivered` would have made it *worse*: a cached response was never delivered anywhere, so the
new name would be actively false where the old one was merely vague.

## Why it matters beyond naming

Two kinds of record sharing one type means every consumer of `Envelope` has to know which kind it
is holding, and nothing in the type says. `RequestBody` and `ClientName` are meaningless on a
cached response; `Response` and `CachedAt` are meaningless on a queued write; `CorrelationId` is
meaningful only on a write. A reader cannot tell which fields are load-bearing without knowing
the kind, and the kind is inferred from a boolean named after something else.

It also made the [issue 25](25-binary-request-response-bodies.md) cache-identity bug harder
to see: cache envelopes and outbox envelopes share an id space, which is why a deterministic
`cache:` prefix was needed to keep them from colliding.

## [66](../66-dead-letter-store-fails-the-scope-test.md) removes two of the flag's three jobs

Added 2026-09-09. **Do 66 first.** It does not merely make this item easier, it changes which candidate shape is cheaper.

`IsSynced` currently does three things:

1. Marks a cache entry, set `true` at creation by `Envelope.ForCachedResponse`.
2. Marks a queued write as delivered, set `true` by `MarkDeliveredAsync`.
3. Pairs with `IsDeadLettered` to define the outbox — `.Where(e => !e.IsSynced && !e.IsDeadLettered)`.

Under 66, a delivered write is **removed** from the store rather than flagged, so job 2 becomes a delete. And the dead-letter partition goes, so `IsDeadLettered` disappears and job 3 loses its other half.

**What is left is job 1 alone** — a flag whose entire meaning is *this is a cache entry, not an outbox entry*. There is no status left for it to plausibly be about, which is this item's thesis with the camouflage removed.

### It also inverts the choice below

The hesitation about two types was that `IHyperwycStore` has one `UpsertAsync` and would need either two or a shared base. Count the interface after 66 removes `MoveToDeadLetterAsync`:

| Method | Applies to |
|---|---|
| `GetCachedResponseAsync` | cache only |
| `InvalidateCacheForPrefixAsync` | cache only |
| `GetPendingOutboxAsync` | outbox only |
| `MarkDeliveredAsync` | outbox only |
| `UpsertAsync` | both |
| `ResetAsync` | both |

**Four of six are already kind-specific.** The interface is two interfaces wearing one type, and splitting `UpsertAsync` would make it more honest rather than more complicated — which removes the reason option 2 was held back.

### The workaround that keeps reappearing

[68](68-cache-invalidation-leaves-tombstones.md)'s fix had to filter on `Response is not null` so that prefix invalidation would not sweep up queued writes matching the same URL. That filter is a **kind check written as a field check**, because there is no kind to check. Expect more of them until there is.

## Candidate shapes

1. **An explicit discriminator** — `Envelope.Kind` of `EnvelopeKind { CachedResponse, QueuedWrite }`.
   Smallest change, makes the intent readable, leaves the irrelevant fields present but
   explicable.
2. **Two types.** `CacheEntry` and `OutboxEntry`, sharing whatever is genuinely common. Honest,
   and makes the irrelevant fields impossible rather than merely ignorable. Larger: `IHyperwycStore`
   currently has one `UpsertAsync`, and would need either two or a shared base.

2 is the better model and 1 is the cheaper step toward it. Prefer 2 unless it turns out to
complicate `IHyperwycStore` more than the clarity is worth.

## Acceptance Criteria

- [x] Nothing distinguishes envelope kinds by negation of a misnamed flag. There is no flag, and
      no negation: the kinds are types.
- [x] A reader can tell from the type which fields are meaningful. Every field on both types is
      load-bearing on every instance of it.
- [x] `IHyperwycStore` stays comprehensible — if the split makes the interface worse, say so and
      take option 1. It did not: seven methods in three named groups, where six of the previous
      seven were kind-specific but did not say so. Splitting `UpsertAsync` was the whole cost.

## Notes

- **Do not fold this into a rename.** It surfaced during one and was deliberately excluded:
  mixing a model change into a mechanical pass is how a rename hides a bug. That reasoning holds
  for whoever picks this up next to something else.
- Sequence after [49](49-unreadable-store-recovery.md) rather than before — 49 is a real defect,
  this is a clarity problem with no known failure attached.
