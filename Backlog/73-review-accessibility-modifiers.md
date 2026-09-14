# Issue 73 — Review Accessibility Modifiers

## Summary

Audit every `public` type and member in `Hyperwyc.Core` and `Hyperwyc` against the convention [36](Done/36-public-surface.md) set: **start internal, widen on demand**. Each public symbol either names the consumer who needs it, or gets narrowed. The audit runs both ways: anything a legitimate consumer needs and cannot reach is a defect too.

Found while implementing [23](Done/23-v1-diagnostics-view.md): `QueuedWrite` is `public`. It has to be, since a custom `IHyperwycStore` implementation cannot be written without it. But nobody had checked whether the rest of the surface is public for a reason, or only by accident.

**The initial review found two things urgent enough to fix straight away, and they are done** (below). What remains is the longer-term work: the full audit, the rules that keep the surface honest afterwards, and a decision on enforcement.

## Status

⬜ Open. The urgent changes were made on 2026-09-15, and the rest has not started.

## Addressed in the initial review

1.0.0 is on NuGet, so strictly every narrowing is a breaking change. Most of the candidates below can wait for the first release that needs a major bump anyway, and [43](43-honour-vary-header.md)'s change to cache keying probably forces one.

The test used for "urgent" was not *does this look wrong*. It was **is this surface people actually use, in a shape we already expect to change?** Surface nobody should be calling costs nothing to narrow whenever it happens, because in practice there is no one to break. Surface people do use gets more expensive to change with every release.

**`HyperwycEvent` and `PendingItem` are no longer positional records.** A positional record fixes its parameter list: adding a field changes the constructor and the `Deconstruct` signature, which is a binary break and a source break for positional patterns. `HyperwycEvent` had already grown once, in [40](Done/40-surface-deferred-outcomes.md), and `PendingItem` is where [69](69-expiring-queued-writes.md) would plausibly show age or expiry. Both now use init-only properties, marked `required` where the constructor parameter was mandatory, which matches `DeliveryOutcome`. Code that reads properties compiles unchanged. Nothing in the docs or sample constructed either type, and in the tests only one helper did (`HyperwycEventStreamTests.MakeEvent`), which is a fair picture of how rarely a consumer would. The only thing that breaks is positional construction and deconstruction, which is exactly what was removed.

## Rules to decide

The audit fixes the surface once. These keep it fixed.

- **How `IHyperwycStore` evolves.** It is necessarily public, and it is the contract most likely to need breaking: [43](43-honour-vary-header.md) changes cache keying from `(url)` to `(url, variant)`, and [42](42-cache-eviction.md) may add eviction operations. Every change hits every third-party store. The candidate rules: new members get default implementations (as `TryQuarantineAsync` already does); changes that cannot be additive wait for a major version; or `storage.md` says outright that the store contract is less stable than the consumer API. The risk is not having a rule and finding out when someone's store breaks.
- **How Core exposes things to the `Hyperwyc` package.** Either public or `InternalsVisibleTo`, decided once instead of symbol by symbol. See `UsesDerivedEncryptionKey` below.
- **Shape of data types that will grow.** The records above were fixed by hand. The rule that stops the next one is "public data types use init-only properties, not positional parameters".

## Who the surface is for

A symbol should be public only if one of these needs it:

1. **An application developer** configuring Hyperwyc through DI and consuming `IHyperwyc`, `IHyperwycDiagnostics` and the event stream.
2. **A store author** implementing `IHyperwycStore` against `Hyperwyc.Core` alone ([storage.md](../docs/storage.md#implementing-your-own)).
3. **Someone constructing `HyperwycHandler` by hand** without Microsoft.Extensions.DependencyInjection. This is the capability 36 decision 4 chose to keep.

There is a fourth reason in the code that is not a consumer: **the `Hyperwyc` package itself.** Core has `InternalsVisibleTo` for its test project only, so anything the Cabinet package needs from Core is public for everyone. That is the reason to be most suspicious of.

## Candidates from a first pass

None of these is urgent by the test above. They are housekeeping for the first release that needs a major bump anyway.

| Symbol | Why it looks wrong | Question to answer |
|---|---|---|
| `StoreHealth` | A public type whose public members are all mutating: `ReportUnreadableAsync`, `Recovered`, `Generation`, `IsStoreFailure`. No consumer should ever call them. It is almost certainly public only because `HyperwycHandler`'s public constructor takes it, which is **the same trap 36 decision 4 hit with the event stream**. It looks worse than it is: nobody can use it meaningfully, so narrowing its members breaks no real consumer. What is actually fixed is the handler's constructor, and 36 already accepted that | Re-run 36's trade. The stream stayed public because its public surface was only `IObservable<T>` and `IDisposable`, with `Publish` already internal. Internal members on a public type, as the stream does, may be the whole answer |
| `HyperwycOptions.UsesDerivedEncryptionKey` | Public setter, and its own XML doc says "set by the storage package; not something to configure". It is public because `CabinetServiceCollectionExtensions` sets it across the assembly boundary. It is also a storage concept on the core options type, which [31](Done/31-package-structure.md) decision 3 ruled out. It only changes the wording of one log line | `InternalsVisibleTo("Hyperwyc")`, which a third-party store could not use in the same way, or move the knowledge onto the store, where it belongs? |
| `QueuedWrite.RetryCount`, `QueuedWrite.LastOutcome` | `set`, while every other property is `init` | Does a store author need to mutate them, or only to round-trip them? If only round-trip, `init` is enough |
| `QueuedWrite.For(HttpRequestMessage)`, `CachedResponse.For(...)`, `PendingItem.From(QueuedWrite)` | Factories used by the handler and the service. A store author persists these types but never builds them from a request, and a diagnostics consumer reads a `PendingItem` but never projects one. `QueuedWrite.For` also blocks on `ReadAsByteArrayAsync` | Is any of them part of anyone's workflow, or is each only a convenience for Core? |
| `PendingItem`, `HyperwycEvent` | Unsealed `record`s, while `DeliveryOutcome` is a `sealed record` and most classes are `sealed` | Is inheritance intended? If not, seal them. That is a narrowing too, so it belongs here |

**Probably justified, but check anyway:** `InMemoryStore` (testing, [testing.md](../docs/testing.md)), `CabinetStore.OpenQuarantined` and `QuarantinePath` (documented recovery path in `storage.md`), `AlwaysOnlineConnectivityService` and `NetworkAvailabilityConnectivityService`.

**Too restrictive:** none found yet. The places this would show up are a store written outside the repo, so the alternatives in [71](71-store-benchmarks-and-alternative-implementations.md) and the conformance suite from [51](Done/51-cabinet-store-not-thread-safe.md), and a handler built without DI.

## Beyond types and members

- **Namespaces are public API here**: folders map to namespaces (36 decision 3). Check that each public type sits where `using Hyperwyc;` plus the obvious second directive finds it.
- **Enforcement.** A second one-off pass will drift the same way as the first. `Microsoft.CodeAnalysis.PublicApiAnalyzers` would put every surface change into the diff as a `PublicAPI.Unshipped.txt` edit, so a widening becomes a reviewed decision instead of a side effect. Adopting it is an option to weigh here, not a given: it adds a file to maintain and a little friction to every public change.

## Acceptance Criteria

- [x] Urgent changes from the initial review made: `HyperwycEvent` and `PendingItem` use init-only properties.
- [ ] An inventory of every public type and member in both assemblies, each with the consumer (from the list above) that needs it.
- [ ] Every symbol without a consumer is narrowed, or kept with the reason written down, in a release that is a major bump anyway.
- [ ] `StoreHealth` and `UsesDerivedEncryptionKey` decided explicitly, since both come from the handler-constructor and cross-assembly constraints rather than from any consumer.
- [ ] The rules above decided and recorded in Conventions: how `IHyperwycStore` evolves, how Core exposes things to the `Hyperwyc` package, and the shape of public data types.
- [ ] A decision on enforcement (analyzer or not), with the reason.
- [ ] XML docs and `docs/` updated for anything narrowed; [58](Done/58-docs-code-reconciliation.md) showed that prose outlives the code it describes.

## Related

- [36](Done/36-public-surface.md): the first pass, and the source of the convention.
- [31](Done/31-package-structure.md): the package boundary that makes cross-assembly access a question.
- [23](Done/23-v1-diagnostics-view.md): where this was noticed.
- [43](43-honour-vary-header.md) and [42](42-cache-eviction.md): the planned `IHyperwycStore` changes the evolution rule has to cover.
- [60](60-glossary.md): a dead-letter rename touches public names too, so sequence the two to avoid breaking the surface twice.
- [71](71-store-benchmarks-and-alternative-implementations.md): out-of-repo store implementations are the real test of whether the surface is too narrow.
