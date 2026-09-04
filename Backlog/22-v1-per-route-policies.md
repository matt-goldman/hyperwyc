# Issue 22 — [v1.0] Per-Route Policies

## Summary

Let a consumer configure caching behaviour per route — strategy, TTL, and whether a write
invalidates the cache — rather than one global setting for every endpoint.

## Background

The single global policy cannot express "cache the catalogue for a day, never cache payments",
which the README already promises. It is one of the two remaining release-candidate blockers.

## This item is stale, and smaller than it looks

Written before the [ADR 0004](../docs/decisions/0004-default-to-removal.md) audit, which deleted
two of the three things it proposed making per-route:

- **`OfflineResponsePolicy` is gone.** One of the three motivating examples — payments returning
  `503` so the flow can handle queued state explicitly — no longer has a mechanism, and does not
  get one back. Synthetic responses have one shape; a caller who wants to know reads
  `X-Hyperwyc-Status`, or the `202`, which no ordinary success is.
- **`TtlStalenessEvaluator` is gone.** Staleness is now a TTL comparison in the handler against
  `HyperwycOptions.DefaultCacheTtl`, so "the evaluator should accept a TTL parameter" describes a
  type that no longer exists.
- **`EmptyOfflineBody` / `ReturnsCollection` is gone** with [issue 26](Done/26-v2-typed-response-shaping.md),
  closed unbuilt.

What is actually left to vary per route: **cache strategy**, **TTL**, and
**invalidate-on-write**.

## Design

**Option A**, a declarative route-to-policy map. Option B — a consumer implementing `ISyncPolicy`
and branching on the request — is explicitly not the intent: *per-request mechanics are by
definition not a policy*. That principle has a consequence the original framing missed.

### `ISyncPolicy` goes

```csharp
public interface ISyncPolicy
{
    CacheStrategy GetStrategy(HttpRequestMessage request);
    bool ShouldInvalidateCacheOnWrite(HttpRequestMessage request);
}
```

Both members take a request, and **every shipped implementation ignores it** —
`SyncPolicy.CacheFirst()` and friends return a `PresetSyncPolicy` holding fixed values. So the
interface is a per-request hook that nothing uses per-request, which is precisely the shape just
ruled out of scope. Keeping it alongside a route map would leave two ways to configure one
thing, the parallel mechanism ADR 0004 exists to prevent.

So this item **removes a public interface while adding the feature**, which is the right
direction of travel.

### The shape

A policy becomes a value rather than a function:

```csharp
public sealed record RoutePolicy
{
    public CacheStrategy Strategy { get; init; } = CacheStrategy.CacheFirst;
    public TimeSpan? Ttl { get; init; }                 // null = HyperwycOptions.DefaultCacheTtl
    public bool InvalidateCacheOnWrite { get; init; } = true;
}
```

Configured as a map, with a default:

```csharp
services.AddHyperwyc(options =>
{
    options.Connectivity = new MauiConnectivityService();

    options.Routes
        .For("/api/reference/*", new RoutePolicy { Ttl = TimeSpan.FromDays(7) })
        .For("/api/products/*",  new RoutePolicy { Ttl = TimeSpan.FromSeconds(30) })
        .For("/api/payments/*",  new RoutePolicy { Strategy = CacheStrategy.NetworkOnly });
});
```

The handler resolves the request to a policy once; `SyncOrchestrator` resolves the same way from
the envelope's URL when deciding whether a replayed write invalidates the cache.

### Matching

- Match on the **path**, ignoring scheme, host and query string — patterns like `/api/notes/*`
  then work regardless of `BaseAddress`, and match what
  [issue 10](Done/10-write-triggered-cache-invalidation.md) already does for invalidation
  prefixes.
- Prefix and suffix wildcards only. No regex, no glob library.
- **Most specific wins**, measured by matched literal length, so `/api/products/detail` beats
  `/api/products/*` beats `/api/*`.
- No match falls back to `HyperwycOptions.DefaultPolicy`.
- Case-insensitive, since a consumer getting that wrong silently is the main hazard of hand-rolled
  matching and is the reason to ship a matcher at all.

### Renames while here

`SyncPolicy.ApiFirst()` becomes `NetworkFirst()`. It is Workbox's name for the same strategy,
Hyperwyc already has `NetworkOnly`, and the current naming diverges from the vocabulary
developers arrive with. Free before release.

## Acceptance Criteria

- [ ] `RoutePolicy` record with `Strategy`, `Ttl` and `InvalidateCacheOnWrite`.
- [ ] A route map on `HyperwycOptions` with a `For(pattern, policy)` builder and a default.
- [ ] `ISyncPolicy`, `SyncPolicy` and `PresetSyncPolicy` removed; nothing takes an
      `HttpRequestMessage` to decide a policy.
- [ ] Handler and orchestrator both resolve through the same matcher.
- [ ] `CacheStrategy.ApiFirst` renamed `NetworkFirst`.
- [ ] Tests: exact match, prefix wildcard, most-specific wins, fallback to default, case
      insensitivity, per-route TTL actually changing staleness, per-route strategy actually
      changing the path taken, and invalidate-on-write honoured per route on both the handler and
      the replay path.
- [ ] README and TECHNICAL_PLAN updated; the "per-route policies are planned" caveat removed.

## Notes

- **Cross-route invalidation is not part of this.** A policy declaring that a write to one route
  invalidates another fails the scope test on "does it depend on something only Hyperwyc knows" —
  it does not, it is the application's domain knowledge — and route-level declaration cannot
  express the data-dependent case anyway (`POST /sales` with `productId=7` should invalidate
  `/products/7`, not all of `/products`). Tracked separately as the absence of any invalidation
  API on `IHyperwyc`; RFC 9111's `Location`/`Content-Location` invalidation is the v1.5 direction,
  alongside [issue 41](41-honour-cacheability-directives.md).
- Check whether per-route TTL dissolves the demand for explicit invalidation before building one.
  A volatile route set to `NetworkFirst` or a 30-second TTL may not need it.
