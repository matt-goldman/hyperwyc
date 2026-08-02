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

- [ ] `SyncPolicy.CacheFirst(TimeSpan.FromDays(1))` results in a 1-day effective TTL.
- [ ] An explicitly assigned `options.StalenessEvaluator` is never overwritten.
- [ ] Setting `options.DefaultCacheTtl` directly, with no preset policy, is honoured.
- [ ] Unit test asserts the effective TTL through `AddHyperwyc` for: preset policy TTL, explicit `DefaultCacheTtl`, and custom evaluator.

## Notes

- Discovered while reconciling the docs against the code; not previously tracked.
- Smallest of the four reconciliation items, and the one most visible to a first-time
  user, since it makes the quick-start snippet misleading.
- Distinguishing "caller set it" from "still the default" may be easiest by making the
  property nullable internally, or by defaulting it to `null` and resolving at registration.
