# Issue 53 — Hyperwyc Is Not AOT-Safe: No `JsonSerializerContext` for the Store

## Summary

`CabinetStore` constructs its store as `new FileOfflineStore(dbDirectory, crypto, null)`,
which resolves to the overload taking an `IIndexProvider?`. Cabinet therefore falls back to its
own default `new JsonSerializerOptions { WriteIndented = false }` — reflection-based
serialisation. Any consumer publishing with AOT or trimming inherits that, from a library whose
primary audience is .NET MAUI, where AOT is the default for iOS release builds.

## Status

⬜ Open. Filed 2026-08-25 by the author, while reading Cabinet's source during
[issue 51](Done/51-cabinet-store-not-thread-safe.md).

## What is actually wrong

Cabinet is explicitly AOT-safe and provides the seam for it — a second constructor:

```csharp
public FileOfflineStore(string rootPath, IEncryptionProvider crypto,
                        JsonSerializerOptions jsonOptions, IIndexProvider? indexer = null)
```

We do not use it. Passing `null` as the third argument binds to the *other* overload, so the
options we never supplied default to a plain reflection-based instance. The failure is silent at
build time and shows up as a trim warning at best, or a runtime serialisation failure on device
at worst — the classic AOT failure shape, where it works in debug and breaks in release.

There is no declaration of intent either: neither `Hyperwyc.Core` nor `Hyperwyc` sets
`IsAotCompatible`, `IsTrimmable` or `EnableTrimAnalyzer`, so nothing warns us that we are
shipping reflection-dependent code.

## What has to be serialised

From the stack in issue 51, the persisted shape is `List<Envelope>` — Cabinet saves the whole
record set as one document. The context therefore has to cover the full graph:

- `List<Envelope>`, `Envelope`
- `CachedResponse`, `DeliveryOutcome`, `DeliveryOutcomeKind`
- `Dictionary<string, string>` (request and response headers)
- `byte[]` (`DeliveryOutcome.Body`), `DateTimeOffset?`, and the enums

Per Cabinet's README, the `JsonSerializerContext` must be **hand-written by the consumer** —
Cabinet's own source generator deliberately does not emit one, because two source generators
cannot reliably coordinate in a single compilation pass. So this is Hyperwyc's to write, not
something a package reference gives us.

It belongs in the `Hyperwyc` package, alongside `CabinetStore`, referencing the model types
from `Hyperwyc.Core`. An `internal` context is sufficient and preferable — it keeps the public
surface unchanged, and referencing public types from an internal context is legal.

## Why this is Hyperwyc's problem and not the consumer's

Through [the scope test](../docs/decisions/README.md#the-standing-scope-test):

| Question | Answer |
|---|---|
| Would the problem exist without Hyperwyc? | **No.** It is our store, our model types, and our call into Cabinet |
| Does it require anything of the consumer's API? | No |
| Can the application already do it? | **No.** `CabinetStore` builds the `FileOfflineStore` internally; a consumer has no way to supply options for types they do not own |
| Does it depend on something only Hyperwyc knows? | **Yes** — the persisted shape is `Envelope`, which is ours |

This is the same shape as [issue 40](Done/40-surface-deferred-outcomes.md): structurally
impossible for the consumer to fix from outside.

## Acceptance Criteria

- [ ] An internal `JsonSerializerContext` in `Hyperwyc` covering the full `List<Envelope>` graph.
- [ ] `CabinetStore` passes `new JsonSerializerOptions { TypeInfoResolver = <context>.Default }`
      through the four-argument `FileOfflineStore` constructor.
- [ ] `IsAotCompatible` (or at minimum `IsTrimmable` + `EnableTrimAnalyzer`) set on both src
      projects, so the compiler tells us next time rather than a consumer's release build.
- [ ] Build is warning-free with the analyzers on — or every remaining warning is understood and
      recorded, not suppressed wholesale.
- [ ] Consider `[AotRecord]` for the generated `IdSelector`, which would replace the hand-written
      `IdSelector = e => e.Id` — convenience only, decide on its merits.

## Notes

- **Suggested milestone v1.0.** Reluctantly, given how deliberately short that list is. The
  argument: MAUI is the primary audience, iOS release builds are AOT by default, and this is the
  category of bug that never appears in development and always appears in the store submission.
  It is also small — a context class, a constructor argument, and two csproj properties. Author's
  call.
- The `null` third argument should go regardless of the milestone. Even keeping today's
  behaviour, `new FileOfflineStore(dir, crypto, indexer: null)` says what it means, where a bare
  `null` reads as "no options" and silently means the opposite.
- **On-disk format compatibility is a non-requirement.** Nothing is released and the only
  consumers are the sample and the tests, so the persisted shape is free to change. If the
  context happens to serialise differently, that is fine — do not spend effort preserving the
  old format.
- Cabinet's README warns that record types and the context need compatible accessibility. Our
  models are public, so an internal context is fine; the rule bites the other way round
  (internal records, public context).
