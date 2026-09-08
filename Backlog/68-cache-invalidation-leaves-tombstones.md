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

⬜ Open. Filed 2026-09-09, found while writing the "implementing your own store" guidance for [storage.md](../docs/storage.md#implementing-your-own) — the contract had to be described precisely enough to reimplement, and describing it made the residue visible.

Both shipped stores behave identically. `InMemoryStore` leaves a dictionary entry; `CabinetStore` writes the husk back to disk.

## It is not unbounded — but the bound is the wrong shape

A tombstone **is** reclaimed if that exact URL is fetched again, because cache envelopes use a deterministic `cache:{url}` id and the next `UpsertAsync` overwrites it. So this is not a straightforward leak.

The problem is which URLs get reclaimed. Invalidation matches a **prefix**, derived from the URL that was written to:

- The app caches `GET /sales/1` … `/sales/500`.
- A `POST /sales` succeeds, so everything under `/sales` is invalidated — 500 tombstones.
- The app then refetches `/sales`, the collection, and moves on.

The 500 individual records are never fetched again and never reclaimed. That shape — cache the items, write to the collection, re-read the collection — is ordinary, not contrived.

## Three ways it costs more than the disk space

**1. It retains request headers indefinitely.** `Envelope.ForCachedResponse` goes through `ForRequest`, which captures `FlattenHeaders(request.Headers)`. So a cache envelope holds the headers of the `GET` that populated it — including an `Authorization` header, which [30](30-sensitive-header-exclusion.md) already documents as persisted. A tombstone keeps them after the entry has stopped being useful for anything, which extends that exposure for no benefit whatsoever. This is the same argument as discarding the request body once a write is delivered ([66](66-dead-letter-store-fails-the-scope-test.md)), arriving from a different direction.

**2. It compounds [52](52-store-rewrites-whole-set-per-write.md).** Cabinet's `RecordSet` calls `SaveAllAsync` for a single-record change, so every `UpdateAsync` in that loop rewrites the entire store. Invalidating 500 entries is 500 full-store rewrites, each one larger because of the tombstones the previous ones left. Invalidation is 52's worst case, and this makes it worse over time.

**3. The loop does not filter to cache entries.** `Where(e => e.Url.StartsWith(prefix))` matches queued writes under the same prefix too. Setting `Response = null` on one is a no-op — it was already null — but it still writes the record back, so a `POST /sales` also pointlessly rewrites the store once per queued `/sales` write it happens to match.

## Candidate fixes

1. **Delete rather than null.** The obvious one, and it needs a store method — `IHyperwycStore` has no delete. Adding one is a public interface change, so it wants doing alongside [55](55-envelope-kind-discriminator.md) rather than on its own.
2. **Filter the loop to cache envelopes**, which fixes cost 3 on its own and is a two-line change independent of everything else.
3. **Fold into eviction** ([42](42-cache-eviction.md)). A store that evicts by age or size collects tombstones as a side effect. Cheapest in effort, slowest in arriving, and it treats a symptom.

1 and 2 together are the real fix. 2 is worth doing whenever, since it is small and unambiguous.

## Acceptance Criteria

- [ ] Invalidating a cached response does not leave a record behind.
- [ ] No request headers survive the entry they belonged to.
- [ ] Invalidation does not write records it has not changed.

## Notes

- **Found by writing documentation, again.** The contract had to be stated precisely enough that someone could reimplement `IHyperwycStore` against it, and "clears the response, not the record" is a sentence you cannot write without noticing what happens to the record. That is now in `storage.md` as a contract detail an implementer must match — which is correct today, and should change when this does.
- Related but distinct from [42](42-cache-eviction.md): that is about a cache that grows because nothing expires. This is about records that are already dead and still occupy space.
