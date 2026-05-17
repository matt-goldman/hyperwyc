# Issue 22 — [v1.0] Per-Route Policies

## Summary

Allow developers to configure per-route policies — including cache TTL, cache strategy, and offline response behaviour — for individual endpoints or URL patterns, overriding the global defaults.

## Background

The global `SyncPolicy.CacheFirst(TimeSpan.FromDays(1))` policy is too coarse for apps where different resources have different requirements. For example:

- `/api/profile/*` may need a 5-minute cache TTL.
- `/api/reference-data/*` could safely be cached for a week.
- `/api/payments/*` should use `OfflineResponsePolicy.Signal` (503) so payment flows can handle queued state explicitly, while all other routes use `Transparent` (200).

This issue expands the original TTL-only scope to cover all per-route policy dimensions, aligning with Hyperwyc's service-worker-inspired design: routes that are safe for transparent handling get the default, routes that need special treatment opt in.

## Design Options (decide during implementation)

### Option A — Fluent policy map in `HyperwycOptions`
```csharp
options.RoutePolicy = new RoutePolicyMap()
    .For("/api/profile/*", route =>
    {
        route.Policy = SyncPolicy.CacheFirst(TimeSpan.FromMinutes(5));
    })
    .For("/api/reference-data/*", route =>
    {
        route.Policy = SyncPolicy.CacheFirst(TimeSpan.FromDays(7));
    })
    .For("/api/payments/*", route =>
    {
        route.OfflineResponsePolicy = OfflineResponsePolicy.Signal;
    });
```

### Option B — `ISyncPolicy` per-request override
`ISyncPolicy.GetStrategy(request)` already receives the `HttpRequestMessage`. A custom `ISyncPolicy` implementation can match URLs and return different policies. This option requires no new infrastructure but places the burden on the developer.

**Recommendation:** Implement Option A as a convenience layer. The route policy map resolves TTL, cache strategy, and offline response policy per route. Option B remains available for advanced cases.

## Acceptance Criteria

- [ ] `RoutePolicyMap` (or equivalent) allows URL-pattern-to-policy mappings.
- [ ] Per-route configuration supports at minimum: `ISyncPolicy`, `OfflineResponsePolicy`, cache TTL override, and an optional `EmptyOfflineBody` (string + content type) returned when the route is read offline with no cached response (see issue #26).
- [ ] Patterns support wildcard suffix matching (e.g. `/api/notes/*`).
- [ ] Most-specific match wins when multiple patterns could apply.
- [ ] Falls back to `HyperwycOptions.DefaultPolicy` / `HyperwycOptions.OfflineResponsePolicy` when no pattern matches.
- [ ] Unit tests cover: exact match, wildcard match, fallback, most-specific wins, per-route offline response policy.

## Notes

- Full regex/glob pattern matching is not required for v1.0; prefix/suffix wildcards are sufficient.
- The staleness evaluator receives the TTL from the matched policy; `TtlStalenessEvaluator` should accept a TTL parameter.
- `OfflineResponsePolicy.Transparent` (200 OK) is the global default. Per-route `Signal` is the opt-in for routes where callers need explicit offline indication.
