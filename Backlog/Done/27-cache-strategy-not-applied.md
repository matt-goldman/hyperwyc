# Issue 27 — `ISyncPolicy.GetStrategy` Is Never Applied

## Summary

`HyperwycHandler` never calls `ISyncPolicy.GetStrategy(request)`. Every read is handled
with cache-first semantics regardless of the configured policy, so three of the four
public `SyncPolicy` factory presets have no effect.

## Background

`SyncPolicy` exposes `CacheFirst(ttl)`, `ApiFirst()`, `CacheOnly()` and `NetworkOnly()`,
each of which constructs a `PresetSyncPolicy` carrying a `CacheStrategy`. The handler
injects `ISyncPolicy` and uses it only for `ShouldInvalidateCacheOnWrite`. `GetStrategy`
has no call site in `src/`.

The practical effect is that a developer who writes
`options.DefaultPolicy = SyncPolicy.NetworkOnly()` still gets cached reads, with no
error and no warning. This is a silent correctness gap in a public API surface, not a
missing feature.

## Behaviour

`HandleOnlineReadAsync` should branch on `_policy.GetStrategy(request)`:

| Strategy | Online read | Offline read |
|---|---|---|
| `CacheFirst` | Serve cache if fresh; otherwise fetch and cache | Serve cache even if stale; otherwise synthetic offline response |
| `ApiFirst` | Fetch; on transport failure fall back to cache | Serve cache even if stale; otherwise synthetic offline response |
| `CacheOnly` | Serve cache regardless of staleness; never send | Same |
| `NetworkOnly` | Always send; never read or write the cache | Synthetic offline response; never consult the cache |

Write paths are unaffected — `CacheStrategy` governs reads only.

## Acceptance Criteria

- [x] `HandleOnlineReadAsync` and `HandleOfflineReadAsync` resolve the strategy via `ISyncPolicy.GetStrategy`.
- [x] `ApiFirst` falls back to cache when the send throws `HttpRequestException`.
- [x] `CacheOnly` never invokes `base.SendAsync`.
- [x] `NetworkOnly` neither reads nor writes the cache, online or offline.
- [x] Unit tests cover each strategy on both the online and offline read paths.

## Resolution

Implemented rather than removed — the presets are useful and the alternative was deleting
public API. Both read paths now resolve `ISyncPolicy.GetStrategy(request)` first.

One decision not anticipated by this item: **a `CacheOnly` read that finds nothing cached
returns a new `X-Hyperwyc-Status: CacheMiss`**, not `Offline`. The device may well be online;
the request was withheld because the route opted out of the network, and reporting `Offline`
would misdescribe that to any caller inspecting the header. The status code follows
`OfflineResponsePolicy` as the other synthetic responses do — `200` under `Transparent`, `503`
under `Signal`.

`CacheFirst` deliberately does not fall back to cache when the network throws, though
`ApiFirst` does. `CacheFirst` has already consulted the cache and judged it stale; serving that
same stale entry on failure would make the TTL meaningless. `ApiFirst` never looked, so the
cache is genuinely new information at that point. This asymmetry is covered by a test so it is
not mistaken for an oversight later.

The response-caching block was extracted to `CacheResponseIfEligibleAsync`, since `CacheFirst`
and `ApiFirst` now share it while `NetworkOnly` and `CacheOnly` bypass it.

## Notes

- Discovered while reconciling the docs against the code; not previously tracked.
- Interacts with issue #22 (per-route policies) — per-route strategy resolution should
  reuse whatever resolution point this issue introduces. Worth doing this one first so
  #22 has a working strategy path to hang route matching off.
- Alternative, if the strategy concept is judged premature: delete `CacheStrategy` and
  the three unused presets rather than implement them. Leaving public no-ops in place
  is the one outcome to avoid.
