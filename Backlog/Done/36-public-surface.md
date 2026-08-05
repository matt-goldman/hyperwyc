# Issue 36 — Public Surface and Project Organisation

## Summary

Shrink the public surface to what consumers actually need, and fix the namespace placement that
made ordinary configuration awkward. Filed and completed pre-release, while both are free to
change.

## Decisions

### 1. `IHyperwyc.FlushAsync()` added; `SyncOrchestrator` made internal

`SyncOrchestrator` was public only because the abstraction was incomplete: `IHyperwyc` exposed
`SyncEvents` and `ResetStoreAsync` but no way to flush, so a "sync now" control had to resolve
the concrete type.

This was not hypothetical. Issue #19 already specified `| "Sync Now" button | Manual flush
trigger via IHyperwyc |` — which could not be built as written. The POC would have hit it.

`FlushAsync` now lives on `IHyperwyc`, and `SyncOrchestrator` is internal. Internal types
register and resolve through DI without ceremony, and the core test project already had
`InternalsVisibleTo`.

### 2. Configuration enums moved to the root namespace

`OfflineResponsePolicy` and `CacheStrategy` were in `Hyperwyc.Models`, so this did not compile
with only `using Hyperwyc;`:

```csharp
services.AddHyperwyc(o => o.OfflineResponsePolicy = OfflineResponsePolicy.Signal);
```

A documented, common configuration action required a second using directive. This repo's own
tests had been writing `Models.OfflineResponsePolicy.Signal` to work around it — the friction
was already visible in the codebase.

Both now live in `Hyperwyc`. `Hyperwyc.Models` keeps only genuine data types: `Envelope`,
`CachedResponse`, `SyncEvent`, `SyncEventType`, `RetryOptions`.

### 3. No `Services/` folder

Considered and rejected. "Services" does not discriminate — `HyperwycHandler`,
`SyncOrchestrator`, `TtlStalenessEvaluator` and `SyncEventStream` all qualify under some
reading, so the folder would not tell a reader where anything belongs.

More decisively, **folders are namespaces in this project** (`Interfaces/` →
`Hyperwyc.Interfaces`, `Models/` → `Hyperwyc.Models`), so adding one is a public API decision
rather than tidying. Twelve files at the project root does not justify that. If the root ever
does need splitting, the only cluster with a real identity is the default implementations of
the pluggable interfaces, which mirror `Interfaces/` one for one.

### 4. `SyncEventStream` stays public

Attempted and reverted. `HyperwycHandler` is necessarily public — consumers name it in
`AddHttpMessageHandler<HyperwycHandler>()` — and its constructor takes a `SyncEventStream`. A
public constructor cannot accept an internal type, so hiding the stream would have forced the
handler's constructor internal too, and DI requires a public constructor unless registration
moves to an explicit factory.

That would have removed the ability to construct `HyperwycHandler` by hand, which is a
legitimate thing to want in an application not using Microsoft.Extensions.DependencyInjection.
`SyncEventStream`'s public surface is only `IObservable<SyncEvent>` plus `IDisposable`; its
`Publish` methods are already internal. Paying a real capability for a near-zero reduction in
surface is a bad trade, so it stays public.

## Guiding principle

**Start internal, widen on demand.** Pre-1.0, anything can be made public later; nothing can be
taken back. Types already internal and correctly so: `HyperwycService`,
`HyperwycResponseFactory`, `HyperwycHostedService`, `SyncPolicy.PresetSyncPolicy`.

## Acceptance Criteria

- [x] `FlushAsync` added to `IHyperwyc` and implemented by `HyperwycService`.
- [x] `SyncOrchestrator` is internal; DI registration and resolution still work.
- [x] `OfflineResponsePolicy` and `CacheStrategy` moved to the `Hyperwyc` namespace.
- [x] Configuration compiles with `using Hyperwyc;` alone.
- [x] Unit test: a flush is reachable through `IHyperwyc` without the concrete orchestrator.
- [x] README documents manual sync and restates that shutdown is not a flush trigger.
- [x] TECHNICAL_PLAN records the consumer-facing surface and the namespace split.

## Notes

- Issue #35 was found during this work: with `SyncOrchestrator` internal, consumers lost the
  last way to supply their own replay transport, which surfaced the fact that
  `AddCoreServices` hardcodes `new HttpClientHandler()`.
- Issue #16 (`ResetStoreAsync` flush coordination) becomes easier now that `HyperwycService`
  holds the orchestrator: the coordination it needs is reachable from where the reset happens.
