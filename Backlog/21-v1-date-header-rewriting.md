# Issue 21 — [v1.0] `Date` Header Rewriting on Cached Responses

**NOTE:** This needs further consideration. Callers may also expect to see the date header that truthfully represents the content. If we implement this, it must be configurable. In the meantime this is deprioritised.

## Summary

When serving a response from the local cache, rewrite the `Date` header to the current time so callers cannot inadvertently rely on the original response timestamp.

## Background

An HTTP response served from Hyperwyc's cache carries headers from when the response was first received. The `Date` header in particular reflects the original server-response time. Callers that inspect `Date` expecting it to represent "now" would see a stale timestamp. Rewriting it prevents subtle bugs.

## Behaviour

- When `HyperwycHandler` constructs an `HttpResponseMessage` from a cached `Envelope.Response`, set the `Date` header to `DateTimeOffset.UtcNow`.
- All other headers from the cached response are preserved unchanged.
- Requests actually sent to the network are not affected — only cache-served responses.
- A custom header (e.g. `X-Hyperwyc-Cached-At`) should be added with the original `CachedAt` timestamp so callers who need it can access it.

## Acceptance Criteria

- [ ] `Date` header on cache-served responses is the current time (within a small tolerance).
- [ ] `X-Hyperwyc-Cached-At` header is added with the original `CachedResponse.CachedAt` value.
- [ ] Headers from the original response are otherwise unchanged.
- [ ] Network-fetched responses are not modified.
- [ ] Unit tests cover: `Date` header value, `X-Hyperwyc-Cached-At` header present, non-cached response unmodified.

## Notes

- The header name `X-Hyperwyc-Cached-At` is a proposal; finalise during implementation.
- This change is intentionally deferred from v0.1 to avoid premature commitment to the header naming convention.
