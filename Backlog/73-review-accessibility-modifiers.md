# Issue 73 — Review Accessibility Modifiers

## Summary

Audit every `public` type and member in `Hyperwyc.Core` and `Hyperwyc` against the convention [36](Done/36-public-surface.md) set: **start internal, widen on demand**. Each public symbol either names the consumer who needs it, or gets narrowed. The audit runs both ways: anything a legitimate consumer needs and cannot reach is a defect too.

Found while implementing [23](Done/23-v1-diagnostics-view.md): `QueuedWrite` is `public`. It has to be, since a custom `IHyperwycStore` implementation cannot be written without it. But nobody had checked whether the rest of the surface is public for a reason, or only by accident.

## Status

⬜ Open. Captured, not yet worked. The candidates below come from a first pass, and none of them is a conclusion.

## Why now

36 was a one-off pass, done before items 22, 23, 49, 55, 62, 66 and 70 had reshaped the surface. Nothing enforces the convention between passes, so whatever those items made public for their own convenience is still public.

**The timing matters more than it did.** 36's premise was "pre-1.0, anything can be made public later; nothing can be taken back". 1.0.0 is now on NuGet, so strictly every narrowing is a breaking change and belongs in a major version. The item is scheduled for v1.2 anyway: it is early days, adoption is minimal, and the most likely candidates are members no consumer should be calling. Each further release makes that argument weaker, which is the reason to do this soon rather than defer it to v2.0+.

## Who the surface is for

A symbol should be public only if one of these needs it:

1. **An application developer** configuring Hyperwyc through DI and consuming `IHyperwyc`, `IHyperwycDiagnostics` and the event stream.
2. **A store author** implementing `IHyperwycStore` against `Hyperwyc.Core` alone ([storage.md](../docs/storage.md#implementing-your-own)).
3. **Someone constructing `HyperwycHandler` by hand** without Microsoft.Extensions.DependencyInjection. This is the capability 36 decision 4 chose to keep.

There is a fourth reason in the code that is not a consumer: **the `Hyperwyc` package itself.** Core has `InternalsVisibleTo` for its test project only, so anything the Cabinet package needs from Core is public for everyone. That is the reason to be most suspicious of.

## Candidates from a first pass

| Symbol | Why it looks wrong | Question to answer |
|---|---|---|
| `StoreHealth` | A public type whose public members are all mutating: `ReportUnreadableAsync`, `Recovered`, `Generation`, `IsStoreFailure`. No consumer should ever call them. It is almost certainly public only because `HyperwycHandler`'s public constructor takes it, which is **the same trap 36 decision 4 hit with the event stream** | Re-run 36's trade. The stream stayed public because its public surface was only `IObservable<T>` and `IDisposable`, with `Publish` already internal. Here the cost side is much larger. Internal members on a public type, as the stream does, may be the whole answer |
| `HyperwycOptions.UsesDerivedEncryptionKey` | Public setter, and its own XML doc says "set by the storage package; not something to configure". It is public because `CabinetServiceCollectionExtensions` sets it across the assembly boundary. It is also a storage concept on the core options type, which [31](Done/31-package-structure.md) decision 3 ruled out | `InternalsVisibleTo("Hyperwyc")`, which a third-party store could not use in the same way, or move the knowledge onto the store, where it belongs? |
| `QueuedWrite.RetryCount`, `QueuedWrite.LastOutcome` | `set`, while every other property is `init` | Does a store author need to mutate them, or only to round-trip them? If only round-trip, `init` is enough |
| `QueuedWrite.For(HttpRequestMessage)`, `CachedResponse.For(...)`, `PendingItem.From(QueuedWrite)` | Factories used by the handler and the service. A store author persists these types but never builds them from a request, and a diagnostics consumer reads a `PendingItem` but never projects one. `QueuedWrite.For` also blocks on `ReadAsByteArrayAsync` | Is any of them part of anyone's workflow, or is each only a convenience for Core? |
| `PendingItem`, `HyperwycEvent` | Unsealed `record`s, while `DeliveryOutcome` is a `sealed record` and most classes are `sealed` | Is inheritance intended? If not, seal them. That is a narrowing too, so it belongs here |

**Probably justified, but check anyway:** `InMemoryStore` (testing, [testing.md](../docs/testing.md)), `CabinetStore.OpenQuarantined` and `QuarantinePath` (documented recovery path in `storage.md`), `AlwaysOnlineConnectivityService` and `NetworkAvailabilityConnectivityService`.

**Too restrictive:** none found yet. The places this would show up are a store written outside the repo, so the alternatives in [71](71-store-benchmarks-and-alternative-implementations.md) and the conformance suite from [51](Done/51-cabinet-store-not-thread-safe.md), and a handler built without DI.

## Beyond types and members

- **Namespaces are public API here**: folders map to namespaces (36 decision 3). Check that each public type sits where `using Hyperwyc;` plus the obvious second directive finds it.
- **Keeping it true afterwards.** A second one-off pass will drift the same way as the first. `Microsoft.CodeAnalysis.PublicApiAnalyzers` would put every surface change into the diff as a `PublicAPI.Unshipped.txt` edit, so a widening becomes a reviewed decision instead of a side effect. Adopting it is an option to weigh here, not a given: it adds a file to maintain and a little friction to every public change.

## Acceptance Criteria

- [ ] An inventory of every public type and member in both assemblies, each with the consumer (from the list above) that needs it.
- [ ] Every symbol without a consumer is narrowed, or kept with the reason written down.
- [ ] `StoreHealth` and `UsesDerivedEncryptionKey` decided explicitly, since both come from the handler-constructor and cross-assembly constraints rather than from any consumer.
- [ ] How Core exposes things to the `Hyperwyc` package is decided once (public, or `InternalsVisibleTo`) and recorded in Conventions, so it stops being decided one symbol at a time.
- [ ] A decision on enforcement (analyzer or not), with the reason.
- [ ] XML docs and `docs/` updated for anything narrowed; [58](Done/58-docs-code-reconciliation.md) showed that prose outlives the code it describes.

## Related

- [36](Done/36-public-surface.md): the first pass, and the source of the convention.
- [31](Done/31-package-structure.md): the package boundary that makes cross-assembly access a question.
- [23](Done/23-v1-diagnostics-view.md): where this was noticed.
- [60](60-glossary.md): a dead-letter rename touches public names too, so sequence the two to avoid breaking the surface twice.
- [71](71-store-benchmarks-and-alternative-implementations.md): out-of-repo store implementations are the real test of whether the surface is too narrow.
