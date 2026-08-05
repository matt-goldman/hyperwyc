# Issue 29 — Policy TTL Does Not Reach the Staleness Evaluator

## Summary

`SyncPolicy.CacheFirst(TimeSpan.FromDays(1))` does not produce a one-day cache. The
default `IStalenessEvaluator` is constructed with a hardcoded 5-minute TTL before
`AddHyperwyc` copies the policy's TTL into `HyperwycOptions.DefaultCacheTtl`, so the
copy has no effect on the evaluator that is actually registered.

## Background

`HyperwycOptions` initialises the property inline:

```csharp
public IStalenessEvaluator StalenessEvaluator { get; set; } =
    new TtlStalenessEvaluator(TimeSpan.FromMinutes(5));
```

`AddHyperwyc` then does:

```csharp
if (options.DefaultPolicy is SyncPolicy.PresetSyncPolicy { Ttl: { } policyTtl })
    options.DefaultCacheTtl = policyTtl;
```

`TtlStalenessEvaluator` captures its TTL into a readonly field at construction, and the
`HyperwycOptions`-taking constructor overload is never reached from the DI path. The
assignment to `DefaultCacheTtl` is therefore write-only.

This affects the exact snippet published in the README, TECHNICAL_PLAN §7 and the
`AddHyperwyc` XML docs — the documented one-day TTL silently behaves as five minutes.

## Behaviour

Construct the default evaluator after options configuration has run, using the resolved
`DefaultCacheTtl`. A developer-supplied `StalenessEvaluator` must still win: only replace
the evaluator when the caller did not set one.

## Acceptance Criteria

- [x] `SyncPolicy.CacheFirst(TimeSpan.FromDays(1))` results in a 1-day effective TTL.
- [x] An explicitly assigned `options.StalenessEvaluator` is never overwritten.
- [x] Setting `options.DefaultCacheTtl` directly, with no preset policy, is honoured.
- [x] Unit test asserts the effective TTL through `AddHyperwyc` for: preset policy TTL, explicit `DefaultCacheTtl`, and custom evaluator.

## Resolution

A second defect surfaced while fixing this one: `DefaultPolicy` defaulted to
`CacheFirst(1 day)` while `DefaultCacheTtl` defaulted to 5 minutes — two defaults for one
concept, disagreeing. Because the propagation step overwrote `DefaultCacheTtl` whenever the
policy carried a TTL, a caller who set `DefaultCacheTtl` and left the policy alone had their
value silently replaced by the *default* policy's 1 day.

Both defects share a root cause — TTL expressed in two places with no precedence rule — and are
fixed together:

- `SyncPolicy.CacheFirst()` (parameterless) added, carrying no TTL, and made the default policy.
- Precedence is now unambiguous: a TTL on the policy wins; otherwise `DefaultCacheTtl` supplies
  it. No explicit-assignment tracking is needed, because the default policy no longer competes.
- `HyperwycOptions.StalenessEvaluator` is nullable and defaults to `null`. The default
  `TtlStalenessEvaluator` is constructed inside `AddHyperwycCore`, after the effective TTL is
  known.
- Effective unconfigured behaviour is unchanged at 5 minutes — which is what the code already
  did, the documented default now simply being true.

Tests assert the TTL the resolved evaluator *applies*, not the value left on the options object,
since the original defect was precisely a case where the property was right and the evaluator
was wrong.

## Notes

- Discovered while reconciling the docs against the code; not previously tracked.
- Smallest of the four reconciliation items, and the one most visible to a first-time
  user, since it makes the quick-start snippet misleading.
- Distinguishing "caller set it" from "still the default" may be easiest by making the
  property nullable internally, or by defaulting it to `null` and resolving at registration.
