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

- [ ] `HandleOnlineReadAsync` and `HandleOfflineReadAsync` resolve the strategy via `ISyncPolicy.GetStrategy`.
- [ ] `ApiFirst` falls back to cache when the send throws `HttpRequestException`.
- [ ] `CacheOnly` never invokes `base.SendAsync`.
- [ ] `NetworkOnly` neither reads nor writes the cache, online or offline.
- [ ] Unit tests cover each strategy on both the online and offline read paths.

## Notes

- Discovered while reconciling the docs against the code; not previously tracked.
- Interacts with issue #22 (per-route policies) — per-route strategy resolution should
  reuse whatever resolution point this issue introduces. Worth doing this one first so
  #22 has a working strategy path to hang route matching off.
- Alternative, if the strategy concept is judged premature: delete `CacheStrategy` and
  the three unused presets rather than implement them. Leaving public no-ops in place
  is the one outcome to avoid.
