# Issue 22 — [v1.0] Per-Endpoint TTL Overrides

## Summary

Allow developers to configure different cache TTLs for individual endpoints or URL patterns, overriding the global `DefaultPolicy` TTL.

## Background

The global `SyncPolicy.CacheFirst(TimeSpan.FromDays(1))` TTL is too coarse for apps where different resources have different freshness requirements. A `/api/profile` endpoint may need a 5-minute TTL, while `/api/reference-data` could safely be cached for a week.

## Design Options (decide during implementation)

### Option A — Fluent policy map in `RestycOptions`
```csharp
options.PolicyMap = new PolicyMap()
    .For("/api/profile/*", SyncPolicy.CacheFirst(TimeSpan.FromMinutes(5)))
    .For("/api/reference-data/*", SyncPolicy.CacheFirst(TimeSpan.FromDays(7)));
```

### Option B — `ISyncPolicy` per-request override
`ISyncPolicy.GetStrategy(request)` already receives the `HttpRequestMessage`. A custom `ISyncPolicy` implementation can match URLs and return different policies. This option requires no new infrastructure but places the burden on the developer.

**Recommendation:** Implement Option A as a convenience layer that wraps a default `ISyncPolicy`. Option B remains available for advanced cases.

## Acceptance Criteria

- [ ] `PolicyMap` (or equivalent) allows URL-pattern-to-policy mappings.
- [ ] Patterns support wildcard suffix matching (e.g. `/api/notes/*`).
- [ ] Most-specific match wins when multiple patterns could apply.
- [ ] Falls back to `RestycOptions.DefaultPolicy` when no pattern matches.
- [ ] Unit tests cover: exact match, wildcard match, fallback, most-specific wins.

## Notes

- Full regex/glob pattern matching is not required for v1.0; prefix/suffix wildcards are sufficient.
- The staleness evaluator receives the TTL from the matched policy; `TtlStalenessEvaluator` should accept a TTL parameter.
