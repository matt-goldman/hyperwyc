# Issue 10 — Write-Triggered GET Cache Invalidation

## Summary

When a mutating request (POST, PUT, PATCH, DELETE) succeeds, automatically invalidate cached GET responses for the same URL prefix.

## Background

After a successful write, the cached read data for the affected resource is likely stale. Restyc invalidates these entries by default so that the next GET returns fresh data rather than a now-incorrect cached response.

## Behaviour

1. After a successful (2xx) mutating request to URL `X`:
   - Call `ISyncStore.InvalidateCacheForPrefixAsync(prefix)` where `prefix` is derived from the request URL (default: the URL path without query string).
2. `ISyncPolicy.ShouldInvalidateCacheOnWrite(request)` gates this behaviour — if it returns `false`, no invalidation occurs.
3. Invalidation removes the `Response` field from matching envelopes but does not delete the envelope itself (the outbox entry is preserved if it exists separately).

## URL Prefix Derivation (Default)

Given a write to `POST /api/notes`, invalidate all cached GET envelopes whose URL starts with `/api/notes`.

Examples:
- Write `PUT /api/notes/42` → invalidates `/api/notes` and `/api/notes/42`.
- The prefix used is the path up to (and including) the resource collection segment, i.e. everything up to the last `/`-delimited numeric or GUID segment is considered a candidate.

> **Note:** The default prefix strategy is "same path prefix". A more sophisticated derivation can be provided via `ISyncPolicy` in a later release.

## Acceptance Criteria

- [ ] Invalidation triggered after every successful 2xx write.
- [ ] Invalidation does not run if `ISyncPolicy.ShouldInvalidateCacheOnWrite` returns `false`.
- [ ] `InvalidateCacheForPrefixAsync` is called with the correct prefix.
- [ ] Invalidation does not affect non-cached envelopes (i.e. pending outbox entries without a `Response`).
- [ ] Unit tests cover: invalidation on success, no invalidation on failure, policy suppression, prefix matching.

## Notes

- The `InvalidateCacheForPrefixAsync` implementation lives in `ISyncStore` (issue #04 / #14).
- Fine-grained cache tagging/invalidation strategies are deferred to v2.0.
