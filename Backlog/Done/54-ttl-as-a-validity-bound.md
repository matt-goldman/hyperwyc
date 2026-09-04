# Issue 54 — TTL Is a Refetch Trigger, Not a Validity Bound

## Summary

`RoutePolicy.Ttl` decides when `CacheFirst` reaches the network. It is **not** consulted offline,
where a stale response is served in preference to none. A route that must never be served stale —
where silently-old data is worse than no data — cannot say so.

## Status

⛔ **Closed the day it was filed, 2026-09-05 — by making the validity bound the only semantic.**

Filed to track the missing second behaviour, then dissolved by removing the first. TTL now says
how old a stored response may be and still be served, online and offline alike; past it, nothing
is served. There is no flag, no dependency on [21](../21-v1-date-header-rewriting.md), and no fifth
strategy.

The case against the old behaviour, once stated plainly, was hard to answer: one value meant two
things with a hidden mode switch, and the mode switch fired exactly when the library is supposed
to be doing its job. "Fetch fresh whenever possible" was always expressible as `NetworkFirst` —
a strategy — so nothing was lost by making TTL mean one thing.

The default rose from five minutes to one day in the same change. As a refetch trigger five
minutes was reasonable; as a validity bound it would make a cache useless for any offline
session longer than a coffee break.

Kept rather than deleted because the analysis below is the reasoning for the current design, and
because the HTTP prior art is worth not rediscovering.

## The two semantics

Both are legitimate, and Hyperwyc only has the first:

| | Online, TTL expired | Offline, TTL expired |
|---|---|---|
| **Refetch trigger** (today) | Fetch | Serve stale |
| **Validity bound** | Fetch | Fail — serve nothing rather than something too old |

So `CacheFirst(TimeSpan.FromMinutes(5))` currently means *"refetch after five minutes when you
can"*, not *"never serve anything older than five minutes"*. That is a reasonable default and a
genuine surprise, so it is now stated plainly in the README and TECHNICAL_PLAN regardless of
whether this issue is ever built.

The second semantic has real cases: a price, a safety configuration, a permission or entitlement
set. In each, serving quietly-stale data is worse than serving none, because an application can
detect "no data" and refuse to proceed but cannot detect "this is four days old".

## Prior art says this is not a strategy

HTTP has exactly this distinction and does **not** express it as a caching strategy. `max-age`
sets the window; `must-revalidate` forbids serving stale once it has passed; `stale-if-error`
explicitly permits it. One TTL, plus a separate decision about whether stale is acceptable.

That argues against a fifth `CacheStrategy` and for either a flag or a server-driven directive.

## Three candidate homes, in the order they should be considered

1. **Expose the age and let the application decide** — [issue 21](../21-v1-date-header-rewriting.md)
   already plans `X-Hyperwyc-Cached-At`. With it, a caller reads the age and applies its own
   tolerance. That hands over what only Hyperwyc knows and leaves the judgement where the domain
   knowledge is, which is the answer [the scope test](../../docs/decisions/README.md#the-standing-scope-test)
   prefers. **Check whether this removes the need before building anything else** — the same move
   that dissolved cross-route invalidation.
2. **Honour `must-revalidate`** — [issue 41](../41-honour-cacheability-directives.md). Server-driven,
   no configuration, correct long-term. No help to a consumer whose API sends no directives.
3. **A `ServeStaleWhenOffline` flag on `RoutePolicy`**, defaulting to `true`. Most convenient,
   and the only one that adds a knob. `false` would return the synthetic offline response, so no
   new response shape is needed.

## Acceptance Criteria

- [ ] Decide whether 21's header removes the need. If it does, close this unbuilt.
- [ ] If not, decide between 41 and a policy flag, and record why.
- [x] Document the current behaviour so it is not a surprise — done at filing, in README and
      TECHNICAL_PLAN.

## Notes

- Raised by the author while reviewing per-route policies
  ([22](22-v1-per-route-policies.md)): *"I would expect ttl to fail a fetch if offline and
  the cached item has expired, yet I think ttl is ignored for offline."*
- Not urgent. The default is defensible and is what a Service Worker does; what was missing was
  saying so.
