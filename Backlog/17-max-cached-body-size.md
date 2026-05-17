# Issue 17 — Maximum Cached Response Body Size Limit

## Summary

Enforce a configurable maximum body size for cached responses. Responses exceeding the limit are returned to the caller but not written to the store.

## Background

Storing unbounded response bodies in a local database is a practical problem, particularly on mobile. A sensible default cap prevents the local store from growing without bound due to large API responses.

## Behaviour

- When a response is eligible to be cached (2xx, read request, not already cached), check the response body size.
- If `body.Length > hyperwycOptions.MaxCachedResponseBodyBytes`:
  - Do **not** write the envelope to the store.
  - Still return the response to the caller (the data is not lost; it is just not cached).
  - Optionally log or publish a debug-level trace event.
- Default: `524288` bytes (512 KB).
- Applies only to cached responses; outbound request bodies are **always** queued regardless of size.

## Acceptance Criteria

- [ ] Body size check implemented inside the response cache write path (issue #09).
- [ ] Default limit `524288` bytes enforced.
- [ ] Responses exceeding the limit are returned to the caller without caching.
- [ ] Limit is read from `hyperwycOptions.MaxCachedResponseBodyBytes`.
- [ ] Outbound request bodies are not subject to this limit.
- [ ] Unit tests cover: under limit → cached, over limit → not cached but returned, exact limit → cached.

## Notes

- This value is hardcoded to 512 KB in v0.1. Making it configurable is a v1.0 change noted in issue #20.
- In v0.1 the value is still read from `hyperwycOptions` (wired to the hardcoded default); this makes the v1.0 change a documentation/exposure update, not a code restructure.
