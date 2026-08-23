# Issue 42 — The Cache Grows Without Bound

## Summary

Hyperwyc caps the size of an individual response body but nothing caps the cache as a whole.
There is no entry limit, no total size limit, and no eviction. A long-lived application caching
every GET accumulates indefinitely.

## The problem

`MaxCachedResponseBodyBytes` rejects a single response over 512 KB. Nothing counts how many
entries exist or what they add up to. `ISyncStore` has no eviction operation at all —
`ResetAsync` deletes everything, which is the only bulk removal available and is far too blunt
to be a growth strategy.

Cache entries are only ever removed by:

- a write invalidating a matching URL prefix, which requires a write to that prefix;
- `ResetStoreAsync`, which clears the outbox and dead-letter queue too.

A stale entry that is never re-requested and never invalidated by a write stays forever. Browse
a thousand product detail pages over a year and all thousand responses are still on the device.

This lands hardest on exactly the platform Hyperwyc targets. A field application, installed once
and used daily for years, on a device where storage is scarce and users notice apps that swell.

## Why we do not inherit a solution

The Service Worker comparison is instructive by inversion. A browser gives an origin a **storage
quota** and evicts under pressure — whole caches can disappear, and well-behaved code expects it.
Workbox's `ExpirationPlugin` (`maxEntries`, `maxAgeSeconds`, `purgeOnQuotaError`) exists on top of
that as a way to stay inside the quota deliberately rather than being culled arbitrarily.

Hyperwyc has no such backstop. Nothing above it is watching, so there is no environment to
cooperate with — the whole job is ours.

## Behaviour

Eviction policy, applied to cache entries only. Outbox and dead-letter entries are *not* cache
and must never be evicted: they are undelivered work, and quietly discarding them would lose
data.

That distinction is currently blurred, because both live in the same `Envelope` collection
separated only by whether `Response` is populated. Any eviction implementation has to be exact
about it.

Suggested controls on `HyperwycOptions`:

| Option | Purpose |
|---|---|
| `MaxCacheEntries` | Upper bound on cached responses |
| `MaxCacheBytes` | Upper bound on total cached body size |
| `MaxCacheAge` | Discard entries older than this regardless of TTL or use |

With least-recently-used as the eviction order, which needs a `LastAccessedUtc` on the cache
entry — currently only `CachedAt` exists, so an entry read every day looks identical to one never
read again.

## Open Questions

1. **When does eviction run?** On write, so the bound is never exceeded; or as a sweep at
   startup and after a flush, which is cheaper but allows temporary overshoot. A sweep is
   probably right for mobile — eviction on every cache write puts work on the request path.
2. **Should `ISyncStore` grow eviction operations, or should the orchestrator drive it through
   existing ones?** Pushing it into the interface makes it a burden on every store implementer,
   including the future IndexedDb and Sqlite providers. Driving it from above keeps implementers
   simple but is chattier.
3. **What are sensible defaults?** Unbounded is indefensible now it has been noticed, but a
   default that silently discards data a developer expected to be there is its own problem. A
   generous default — a few thousand entries, a few tens of megabytes — plus a documented
   `OnEvicted` signal would make it observable rather than mysterious.
4. **Does eviction deserve a sync event?** A cached read that used to succeed offline and now
   does not is exactly the kind of behaviour change that is baffling without a trace of why.

## Acceptance Criteria

- [ ] Cache entries are bounded by count and by total size.
- [ ] Eviction never removes outbox or dead-letter entries, only cached responses.
- [ ] Eviction order is least-recently-used, with access time tracked.
- [ ] Bounds are configurable, with documented defaults.
- [ ] Decisions recorded on the open questions above.
- [ ] Unit test: exceeding the entry bound evicts the least recently used entry.
- [ ] Unit test: exceeding the byte bound evicts until under it.
- [ ] Unit test: a pending outbox entry is never evicted, whatever the bounds.
- [ ] Unit test: reading a cached entry updates its access time and protects it from eviction.
- [ ] `CabinetSyncStore` and `InMemorySyncStore` both honour the policy.
- [ ] README documents the bounds and what happens when they are hit.

## Notes

- Found while auditing Hyperwyc against Service Worker and Workbox for gaps.
- Interacts with [issue 23](23-v1-diagnostics-view.md): a diagnostics view showing cache size and
  entry count would make this tractable to reason about, and is most of the work of measuring it.
- Interacts with [issue 32](32-default-encryption-key.md) only indirectly, but the two together
  are the answer to "how much of my users' data is sitting on this device, and how well is it
  protected" — a question a developer adopting an offline library should be able to answer.

## Additional pressure: Android's backup quota

[Issue 48](48-exclude-store-from-os-backup.md) notes that Android Auto Backup caps an app at
25 MB and, on exceeding it, silently stops backing up **the entire app** rather than just the
offending files. An unbounded response cache can therefore take an app's settings and databases
down with it.

Excluding the store from backup fixes that, and is what 48 recommends. But it is another argument
for bounding the cache regardless: a consumer who has not read that advice should not be able to
break something unrelated by caching too much.
