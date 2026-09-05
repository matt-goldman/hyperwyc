# Issue 46 — Use `ETag` and `Last-Modified` for Conditional Revalidation

## Summary

Hyperwyc stores response headers, including `ETag` and `Last-Modified`, and never uses them. Every
refresh of a stale entry re-downloads the whole body, even when nothing has changed.

## The problem

When `IStalenessEvaluator` judges an entry stale, the handler issues an ordinary request and the
server sends the full response. If the resource has not changed, the entire body crosses the
network for nothing.

The information needed to avoid it is already in the store — `Envelope.Response.Headers` captures
everything the server sent, `ETag` included. There is even a test helper that populates one. It is
simply never read.

For the target platform this is not a micro-optimisation. A field application waking on a weak
mobile connection and refreshing a catalogue pays for the whole payload every time, in bytes,
battery and latency, where a `304 Not Modified` would cost a few hundred bytes.

## Behaviour

When revalidating a cached entry:

- Send `If-None-Match` with the stored `ETag` where one exists.
- Send `If-Modified-Since` with the stored `Last-Modified` where there is no `ETag`.
- On `304 Not Modified`, do not treat it as a failure or as a new response. Refresh the entry's
  `CachedAt` so it counts as fresh again, merge any updated headers the `304` carried, and serve
  the stored body.
- On `200`, replace the entry as today.

The `304` handling is the part most easily got wrong. A `304` is not a successful response with an
empty body; treating it as one would cache emptiness over good data. It also is not a failure. It
means "what you have is current", and the correct action is to renew the entry's freshness without
touching its body.

## Open Questions

1. **What does the caller receive on a `304`?** Returning the stored `200` with its body is the
   only sensible answer — the caller asked for the resource, not for a revalidation — but it means
   the response Hyperwyc returns differs from the one it received, which should be documented
   rather than surprising.
2. **Which headers does a `304` update?** RFC 9111 says a `304` may carry updated headers that
   should be merged into the stored response. Merging is more correct; ignoring them is simpler
   and rarely wrong. Worth an explicit decision.
3. **Does this apply to the replay path too?** Replayed writes are not conditional requests, so
   no. But a `412 Precondition Failed` from an application's own `If-Match` on a queued write is a
   real scenario, and belongs with [issue 40](Done/40-surface-deferred-outcomes.md)'s outcome
   reporting rather than here.
4. **Interaction with `Vary`** ([issue 43](43-honour-vary-header.md)): the conditional headers must
   come from the entry matching the *current* request's variant, not merely the same URL.

## Acceptance Criteria

- [ ] `If-None-Match` is sent when revalidating an entry with a stored `ETag`.
- [ ] `If-Modified-Since` is sent when there is a `Last-Modified` and no `ETag`.
- [ ] A `304` refreshes the entry's freshness and serves the stored body.
- [ ] A `304` is not recorded as a failure, and does not overwrite the body with an empty one.
- [ ] A `200` replaces the entry as it does today.
- [ ] Decisions recorded on the open questions above.
- [ ] Unit test: revalidating an entry with an `ETag` sends `If-None-Match`.
- [ ] Unit test: a `304` results in the original body being served and the entry becoming fresh.
- [ ] Unit test: a `200` replaces the cached body.
- [ ] Unit test: an entry with no validators revalidates unconditionally, as today.
- [ ] [docs/caching.md](../docs/caching.md) documents conditional revalidation.

## Notes

- Found while auditing Hyperwyc against Service Worker and Workbox for gaps.
- Composes with [issue 45](45-stale-while-revalidate.md) and
  [issue 41](41-honour-cacheability-directives.md): cheap revalidation is what makes
  stale-while-revalidate affordable to run often, and what makes honouring `no-cache` reasonable
  rather than punitive.
- Also relevant to [issue 42](42-cache-eviction.md): entries that revalidate to `304` are proven
  to still be wanted, which is useful signal for a least-recently-used policy.
