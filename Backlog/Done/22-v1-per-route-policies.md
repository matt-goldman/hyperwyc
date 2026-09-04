# Issue 22 — [v1.0] Per-Route Policies

## Status

✅ **Done.** 2026-09-05. The last release-candidate blocker.

## Summary

Let a consumer configure caching behaviour per route — strategy, TTL, and whether a write
invalidates the cache — rather than one global setting for every endpoint.

## Background

The single global policy cannot express "cache the catalogue for a day, never cache payments",
which the README already promises. It is one of the two remaining release-candidate blockers.

## This item is stale, and smaller than it looks

Written before the [ADR 0004](../../docs/decisions/0004-default-to-removal.md) audit, which deleted
two of the three things it proposed making per-route:

- **`OfflineResponsePolicy` is gone.** One of the three motivating examples — payments returning
  `503` so the flow can handle queued state explicitly — no longer has a mechanism, and does not
  get one back. Synthetic responses have one shape; a caller who wants to know reads
  `X-Hyperwyc-Status`, or the `202`, which no ordinary success is.
- **`TtlStalenessEvaluator` is gone.** Staleness is now a TTL comparison in the handler against
  `HyperwycOptions.DefaultCacheTtl`, so "the evaluator should accept a TTL parameter" describes a
  type that no longer exists.
- **`EmptyOfflineBody` / `ReturnsCollection` is gone** with [issue 26](26-v2-typed-response-shaping.md),
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

### Matching: later rules refine earlier ones

Registration order decides, and a map is written **general to specific** — the pyramid, widest at
the top. This is `.gitignore`'s model and the CSS cascade's, and it matches how the configuration
is composed: state the general rule, then carve out exceptions.

The first draft had it the other way, first-match-wins, which forces the funnel — exceptions
written before the rule they except from. Both work; the pyramid is the order people think and
write in, so it is the one that keeps a long map readable.

**Neither is described as first- or last-match.** That taxonomy names the mechanism and buries
the intent, and "last match wins" reads as surprising when the behaviour is not. The docs say
*register from general to specific; each rule refines the ones before it*, with precedence stated
as a consequence.

Resolution by computed specificity was considered and rejected — and with this pattern language
it would buy nothing. Patterns are exact paths or a prefix ending `/*`, so any two that both
match a path necessarily nest, which means "more specific" and "registered later" always agree.
Computing specificity would be an invisible rule producing the answer a visible one already gives.

- Matched on `AbsolutePath`, case-insensitive, ignoring scheme, host, port and query string, so a
  pattern is independent of the client's `BaseAddress`.
- `/api/sales/*` matches `/api/sales` as well as everything beneath it. A separate entry for the
  collection would be a papercut with no upside.
- `*` matches everything.
- Implemented by iterating the list backwards rather than prepending on registration, so the
  stored order matches the written order for anything that later reads it.

### `NetworkOnly` governs writes

An offline write to a `NetworkOnly` route is **not queued**; it goes to the transport and fails as
it would without Hyperwyc installed.

This restores the payments case the original item named, which lost its mechanism when
`OfflineResponsePolicy` was deleted — and restores it better. `Signal` only changed the status
code on a write Hyperwyc had already taken custody of. Declining custody is the honest answer
where deferral is wrong, and it needs no new member: `NetworkOnly` already meant "do not involve
the store", and now means it in both directions.

Considered and not built: a separate `QueueWritesOffline` flag, and verb-scoped registration
(`For(route, policy, verb)`). Verb scoping would make half of `CacheStrategy` invalid depending on
verb — you do not serve a POST from cache — and make matching two-dimensional, for granularity
finer than read-versus-write that no one has asked for. If it is ever wanted it can be added over
a policy type that is already coherent; the reverse is harder.

## Acceptance Criteria

- [x] `RoutePolicy` record with `Strategy`, `Ttl` and `InvalidateCacheOnWrite`, plus factories for the four strategies. `with` covers the rest.
- [x] `HyperwycOptions.Routes`, a `RoutePolicyMap` with a fluent `For(pattern, policy)` and a `Default`.
- [x] `ISyncPolicy`, `SyncPolicy` and `PresetSyncPolicy` removed; nothing takes an
      `HttpRequestMessage` to decide a policy. `HyperwycOptions.DefaultPolicy` and
      `DefaultCacheTtl` went with them — six members left on the options class.
- [x] Handler and orchestrator both resolve through the same matcher, so a replayed write honours its route's invalidation decision.
- [x] `CacheStrategy.ApiFirst` renamed `NetworkFirst`.
- [x] Tests: `RoutePolicyMapTests` covers matching — exact, wildcard, `*`, refinement order,
      the mis-ordered failure mode, three-level nesting, duplicate patterns, path-only matching,
      case insensitivity and guards. `PerRoutePolicyTests` covers behaviour — strategy and TTL
      varying per route, `NetworkOnly` declining an offline write while other routes still queue,
      and invalidate-on-write honoured per route on both the direct and replay paths.
- [x] README and TECHNICAL_PLAN updated; the "per-route policies are planned" caveat removed.

## Notes

- **Cross-route invalidation is not part of this.** A policy declaring that a write to one route
  invalidates another fails the scope test on "does it depend on something only Hyperwyc knows" —
  it does not, it is the application's domain knowledge — and route-level declaration cannot
  express the data-dependent case anyway (`POST /sales` with `productId=7` should invalidate
  `/products/7`, not all of `/products`). Tracked separately as the absence of any invalidation
  API on `IHyperwyc`; RFC 9111's `Location`/`Content-Location` invalidation is the v1.5 direction,
  alongside [issue 41](../41-honour-cacheability-directives.md).
- Check whether per-route TTL dissolves the demand for explicit invalidation before building one.
  A volatile route set to `NetworkFirst` or a 30-second TTL may not need it. Now testable: the
  sample's stale-catalogue problem should be answerable with a short TTL on `/products` rather
  than an invalidation API.
- **Net removal.** Three public types and two options members out, two types and one member in.
  Registration-time TTL reconciliation — which *was* [issue 29](29-default-ttl-propagation.md)
  — is deleted rather than fixed, because a resolved policy carries one concrete TTL and there is
  nothing left to reconcile.
