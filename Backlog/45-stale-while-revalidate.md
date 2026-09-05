# Issue 45 — `StaleWhileRevalidate` Strategy

## Summary

Add the one Workbox strategy Hyperwyc does not have: serve the cached response immediately, and
refresh it in the background so the next read is current.

## The problem

`SourcePriority` currently offers `CacheFirst`, `ApiFirst`, `CacheOnly` and `NetworkOnly` — Workbox's
set minus `StaleWhileRevalidate`, which is the one its documentation reaches for most often, and
the best fit for the screen a user looks at most.

Today a catalogue screen has to choose between two unsatisfying behaviours:

| Strategy | Behaviour | Cost |
|---|---|---|
| `CacheFirst(short ttl)` | Instant while fresh, then a blocking fetch | The user waits, periodically and unpredictably |
| `CacheFirst(long ttl)` | Always instant | Data goes stale for as long as the TTL allows |
| `ApiFirst` | Always current | Every view waits for the network |

Stale-while-revalidate collapses the trade-off: render from cache with no wait, fetch in the
background, update when it arrives. The screen is never blank and never indefinitely stale.

It is also a real HTTP concept, not just a client-side pattern — `Cache-Control:
stale-while-revalidate` is standardised in RFC 5861, which makes the naming familiar and gives
[issue 41](41-honour-cacheability-directives.md) a directive it could eventually honour.

## Behaviour

On a read with `SourcePriority.StaleWhileRevalidate`:

1. A cached entry exists — return it immediately, **regardless of staleness**, and start a
   background fetch.
2. The background fetch succeeds — update the cache and publish `OnUpdated`.
3. No cached entry — behave as `CacheFirst` does with a cache miss: fetch, cache, return.
4. Offline — return the cached entry and do not attempt a fetch.

The refresh must not block the caller, must not throw into the caller's call stack, and must not
turn a background failure into a visible error. The caller already has a usable response; a
failed refresh means only that the next read is served from the same entry.

## The event carries the weight

`OnUpdated` already exists and is described as "cached response refreshed from API", but it has
had nothing meaningful to fire from — with `CacheFirst` a refresh only happens on a read the
caller was already waiting for.

With this strategy the event becomes the mechanism by which a UI learns to re-render, so it needs
to say enough to be actionable: which URL, and ideally that the content actually changed rather
than merely being re-fetched identically. Workbox's `BroadcastUpdatePlugin` exists precisely for
this, and compares response bodies so it can stay quiet when nothing changed.

That makes this issue dependent on [issue 40](Done/40-surface-deferred-outcomes.md), which is already
enriching the event payload. An `OnUpdated` that fires on every background refresh regardless of
whether anything differs will train consumers to ignore it.

## Open Questions

1. **Should the refresh be suppressed while the entry is fresh?** Revalidating on every single
   read is wasteful for a screen the user scrolls repeatedly. A minimum interval, or reusing the
   TTL as "only revalidate once stale", keeps the instant response while bounding the traffic.
   The latter is probably right and is what most implementations do.
2. **Should `OnUpdated` fire when the refresh produced identical content?** Comparing bodies costs
   a comparison but saves the UI a pointless re-render. Suggest comparing, and adding a flag for
   whether the content changed.
3. **How does a background refresh interact with the flush semaphore and cancellation?** It is
   fire-and-forget work started from a request path, so it needs the same care that the follow-up
   pass in [issue 38](Done/38-retry-classification.md) needed: cancellable on disposal, no
   unobserved exceptions.
4. **Does it deserve to be the default?** It is the best default for most read-heavy screens, but
   changing `CacheFirst`'s meaning now would be surprising. Suggest adding it as an option and
   revisiting the default separately, with evidence.

## Acceptance Criteria

- [ ] `SourcePriority.StaleWhileRevalidate` added and honoured on the read path.
- [ ] A cached entry is returned immediately regardless of staleness.
- [ ] The refresh happens in the background and never blocks or faults the caller.
- [ ] A failed background refresh leaves the cached entry intact and does not surface as an error.
- [ ] Offline, the cached entry is served and no fetch is attempted.
- [ ] `OnUpdated` carries enough for a UI to act, including whether the content changed.
- [ ] Decisions recorded on the open questions above.
- [ ] Unit test: a stale entry is returned without waiting, and the cache is updated afterwards.
- [ ] Unit test: a failing background refresh does not affect the returned response.
- [ ] Unit test: disposal cancels an in-flight background refresh without an unobserved exception.
- [ ] README documents it alongside the other strategies, with the catalogue-screen case as the
      motivating example.

## Notes

- Found while auditing Hyperwyc against Service Worker and Workbox for gaps.
- Composes with [issue 46](46-conditional-requests.md): a revalidation that sends `If-None-Match`
  and gets a `304` costs almost nothing, which makes frequent revalidation affordable. The two
  together are the largest available improvement to perceived responsiveness.
