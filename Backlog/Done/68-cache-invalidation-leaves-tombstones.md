# Issue 68 — Cache Invalidation Leaves Records Nothing Can Reach

## Summary

`InvalidateCacheForPrefixAsync` clears the **response** and keeps the **envelope**:

```csharp
// CabinetStore
foreach (var envelope in all.Where(e => e.Url.StartsWith(urlPrefix, StringComparison.Ordinal)).ToList())
{
    envelope.Response = null;
    await _records.UpdateAsync(envelope.Id, envelope, ct).ConfigureAwait(false);
}
```

What is left is a record no query returns and nothing deletes. `GetCachedResponseAsync` skips it because `Response` is null; `GetPendingOutboxAsync` skips it because a cache envelope carries `IsSynced = true`. It occupies the store until something happens to overwrite it.

## Status

✅ **Done.** 2026-09-09, on the `upgrade-cabinet` branch, ahead of
[70](70-move-bodies-to-cabinet-attachments.md) rather than after it — see below. Filed 2026-09-09, found while writing the "implementing your own store" guidance for [storage.md](../../docs/storage.md#implementing-your-own) — the contract had to be described precisely enough to reimplement, and describing it made the residue visible.

Both shipped stores behave identically. `InMemoryStore` leaves a dictionary entry; `CabinetStore` writes the husk back to disk.

## It is not unbounded — but the bound is the wrong shape

A tombstone **is** reclaimed if that exact URL is fetched again, because cache envelopes use a deterministic `cache:{url}` id and the next `UpsertAsync` overwrites it. So this is not a straightforward leak.

The problem is which URLs get reclaimed. Invalidation matches a **prefix**, derived from the URL that was written to:

- The app caches `GET /sales/1` … `/sales/500`.
- A `POST /sales` succeeds, so everything under `/sales` is invalidated — 500 tombstones.
- The app then refetches `/sales`, the collection, and moves on.

The 500 individual records are never fetched again and never reclaimed. That shape — cache the items, write to the collection, re-read the collection — is ordinary, not contrived.

## Three ways it costs more than the disk space

**1. It retains request headers indefinitely.** `Envelope.ForCachedResponse` goes through `ForRequest`, which captures `FlattenHeaders(request.Headers)`. So a cache envelope holds the headers of the `GET` that populated it — including an `Authorization` header, which [30](../30-sensitive-header-exclusion.md) already documents as persisted. A tombstone keeps them after the entry has stopped being useful for anything, which extends that exposure for no benefit whatsoever. This is the same argument as discarding the request body once a write is delivered ([66](../66-dead-letter-store-fails-the-scope-test.md)), arriving from a different direction.

**2. It compounds [52](52-store-rewrites-whole-set-per-write.md).** Cabinet's `RecordSet` calls `SaveAllAsync` for a single-record change, so every `UpdateAsync` in that loop rewrites the entire store. Invalidating 500 entries is 500 full-store rewrites, each one larger because of the tombstones the previous ones left. Invalidation is 52's worst case, and this makes it worse over time.

**3. The loop does not filter to cache entries.** `Where(e => e.Url.StartsWith(prefix))` matches queued writes under the same prefix too. Setting `Response = null` on one is a no-op — it was already null — but it still writes the record back, so a `POST /sales` also pointlessly rewrites the store once per queued `/sales` write it happens to match.

## Candidate fixes

1. **Delete rather than null.** The obvious one, and it needs a store method — `IHyperwycStore` has no delete. Adding one is a public interface change, so it wants doing alongside [55](../55-envelope-kind-discriminator.md) rather than on its own.
2. **Filter the loop to cache envelopes**, which fixes cost 3 on its own and is a two-line change independent of everything else.
3. **Fold into eviction** ([42](../42-cache-eviction.md)). A store that evicts by age or size collects tombstones as a side effect. Cheapest in effort, slowest in arriving, and it treats a symptom.

1 and 2 together are the real fix. 2 is worth doing whenever, since it is small and unambiguous.

## What was done: 1 and 2, and no interface change was needed

Both, together, and the premise that 1 needed a public interface change was wrong. `IHyperwycStore` has no *general* delete, which is what [55](../55-envelope-kind-discriminator.md) would bring — but `InvalidateCacheForPrefixAsync` **is already a store method**, and a store deleting its own records inside it needs nothing added. The method's own summary said "Removes all cached GET responses"; nulling the response was the less faithful reading of a contract that was already written.

2 turned out to be a precondition rather than an independent nicety. Nulling an already-null `Response` on a queued write was harmless, so the missing filter was pure waste; **removing** an unfiltered match would delete pending writes. The filter is now load-bearing, and is on `Response is not null` rather than on the `cache:` id prefix — it selects exactly what the method exists to invalidate, and can therefore never reach a queued write or a dead-lettered one.

## Why it went before issue 70 rather than after

[70](70-move-bodies-to-cabinet-attachments.md) moves bodies into Cabinet attachments, and Cabinet cleans attachments up on `RemoveAsync` — not on `UpdateAsync`. Had 70 landed first, invalidation would have needed its own explicit blob deletion, written specifically to compensate for the tombstone, and then deleted again by this item. Doing this first meant 70's invalidation path needed no attachment handling at all.

## Acceptance Criteria

- [x] Invalidating a cached response does not leave a record behind.
- [x] No request headers survive the entry they belonged to.
- [x] Invalidation does not write records it has not changed.

## How it is held

`InvalidateCacheForPrefixAsync_RemovesTheRecordAndItsBody` measures the record document against an empty-store baseline after invalidating 20 entries, because "no longer reachable" and "no longer there" are precisely the two states the old behaviour could not be told apart in through the interface. `InvalidateCacheForPrefixAsync_LeavesQueuedWritesAndTheirBodiesAlone` covers the filter. Both fail against the previous implementation; verified by reverting it, not assumed.

`InMemoryStore` gets the same behaviour and `InvalidateCacheForPrefixAsync_LeavesQueuedWritesAlone`, per storage.md's own instruction to test against both implementations. Its existing tests passed unchanged, which is the right outcome — they asserted the contract through `GetCachedResponseAsync`, not the mechanism.

## What this does not fix

**Cost 2 is unchanged.** `RecordSet.RemoveAsync` calls `SaveAllAsync` like everything else, so invalidating 500 entries is still 500 full-set rewrites. [70](70-move-bodies-to-cabinet-attachments.md) makes each of those rewrites small by taking the bodies out, and [42](../42-cache-eviction.md) bounds how many there are; neither makes it one write, and Cabinet has no batch remove. Recorded rather than solved — see [ADR 0008](../../docs/decisions/0008-shrink-what-you-store.md).

## Notes

- **storage.md's contract row changed with the code**, as the note below predicted it should: `InvalidateCacheForPrefixAsync` now reads "Removes the record, and only records that actually have a `Response`", and the interface carries the same in XML docs, since an implementer who nulls instead reintroduces this.
- **Found by writing documentation, again.** The contract had to be stated precisely enough that someone could reimplement `IHyperwycStore` against it, and "clears the response, not the record" is a sentence you cannot write without noticing what happens to the record. That is now in `storage.md` as a contract detail an implementer must match — which is correct today, and should change when this does.
- Related but distinct from [42](../42-cache-eviction.md): that is about a cache that grows because nothing expires. This is about records that are already dead and still occupy space.
