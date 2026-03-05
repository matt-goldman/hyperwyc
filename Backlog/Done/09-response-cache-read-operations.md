# Issue 09 — Response Cache for Read Operations

## Summary

Implement the read-side cache: store GET/HEAD/OPTIONS responses, serve them when fresh, and refresh them when stale.

## Background

Restyc caches responses to read requests so that the app can continue operating while offline or while a network round-trip is in progress. The cache is keyed by URL (normalised). Staleness is evaluated by `IStalenessEvaluator`.

## Cache Flow

```
Incoming GET/HEAD/OPTIONS request
           │
           ▼
   Cached envelope exists?
     ├── Yes → IStalenessEvaluator.IsStale?
     │           ├── No  → Return cached response immediately
     │           └── Yes → Fetch from API, update cache, publish OnUpdated, return fresh response
     └── No  → Fetch from API, store response, publish OnUpdated, return response
```

## Behaviour Details

- **Cache key:** The full request URL (query string included). URL normalisation (e.g. trailing slash) can be deferred to a later issue.
- **Serving from cache:** Construct an `HttpResponseMessage` from `Envelope.Response`. All original headers are preserved. The `Date` header rewriting is a v1.0 feature (issue #21).
- **Cache miss or stale:** Call `base.SendAsync`, then call `ISyncStore.UpsertAsync` with the new `CachedResponse`.
- **Body size limit:** Responses exceeding `RestycOptions.MaxCachedResponseBodyBytes` (default 512 KB) are **not** stored but are still returned to the caller. See issue #17.
- **Non-2xx responses:** Not cached.

## Default `IStalenessEvaluator`

Provide a `TtlStalenessEvaluator` default implementation that uses `CachedResponse.CachedAt + configuredTtl` to determine staleness.

## Acceptance Criteria

- [x] `TtlStalenessEvaluator : IStalenessEvaluator` implemented in `src/Restyc`.
- [x] Cache-hit path (fresh): network call not made, cached `HttpResponseMessage` returned.
- [x] Cache-hit path (stale): network call made, cache entry updated, `OnUpdated` published.
- [x] Cache-miss path: network call made, entry stored, `OnUpdated` published.
- [x] Non-2xx response not cached.
- [x] Write requests bypass this logic entirely.
- [x] Unit tests cover all four paths above.

## Notes

- `IStalenessEvaluator` is pluggable; the default uses TTL from `RestycOptions`.
- Per-endpoint TTL overrides are a v1.0 feature (issue #22).
