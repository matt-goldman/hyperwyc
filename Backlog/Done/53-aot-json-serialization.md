# Issue 53 — Hyperwyc Is Not AOT-Safe: No `JsonSerializerContext` for the Store

## Summary

`CabinetStore` constructs its store as `new FileOfflineStore(dbDirectory, crypto, null)`,
which resolves to the overload taking an `IIndexProvider?`. Cabinet therefore falls back to its
own default `new JsonSerializerOptions { WriteIndented = false }` — reflection-based
serialisation. Any consumer publishing with AOT or trimming inherits that, from a library whose
primary audience is .NET MAUI, where AOT is the default for iOS release builds.

## Status

✅ **Done.** 2026-09-09. Filed 2026-08-25 by the author, while reading Cabinet's source during
[issue 51](51-cabinet-store-not-thread-safe.md).

Nothing about the fix came from Cabinet 2.0. The upgrade was taken first on the expectation that
2.0 addressed this, and it does not: `FileOfflineStore` still defaults to
`new JsonSerializerOptions { WriteIndented = false }` on the three-argument constructor, and
Cabinet still deliberately declines to emit a `JsonSerializerContext` for the consumer. The seam
this item describes is the same seam 1.x had. The work was Hyperwyc's, exactly as filed.

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

Through [the scope test](../../docs/decisions/README.md#the-standing-scope-test):

| Question | Answer |
|---|---|
| Would the problem exist without Hyperwyc? | **No.** It is our store, our model types, and our call into Cabinet |
| Does it require anything of the consumer's API? | No |
| Can the application already do it? | **No.** `CabinetStore` builds the `FileOfflineStore` internally; a consumer has no way to supply options for types they do not own |
| Does it depend on something only Hyperwyc knows? | **Yes** — the persisted shape is `Envelope`, which is ours |

This is the same shape as [issue 40](40-surface-deferred-outcomes.md): structurally
impossible for the consumer to fix from outside.

## What was done

**`HyperwycJsonContext`** — an internal, sealed, partial `JsonSerializerContext` in `Hyperwyc`,
declaring `List<Envelope>` and `Envelope`. Only `List<Envelope>` actually crosses the serialiser
— `RecordSet` saves the whole set as one document — and the generator walks the graph from there,
so `CachedResponse`, `DeliveryOutcome`, `DeliveryOutcomeKind` and the dictionaries did not need
declaring. The element type is declared anyway because it is what names the persisted shape.

**`CabinetStore` passes the options** through the four-argument constructor, with `indexer: null`
named rather than positional. The bare `null` is what caused this: it bound to the *other*
overload, so the argument that looked like "no options" was in fact "no indexer, and default
options".

**`IsAotCompatible` on both src projects.** This found a real defect immediately, which is the
argument for having it: `AddHyperwycCore<TStore>` passes `TStore` to `TryAddSingleton`, whose
`TImplementation` is annotated `DynamicallyAccessedMemberTypes.PublicConstructors`. The trimmer
was free to remove the constructor the container then looks for — a custom store failing to
resolve in a trimmed build, from an API whose whole purpose is that the container constructs the
type. `TStore` now carries the matching annotation, which also propagates the requirement to the
call site, where the concrete type is written and therefore visible to the trimmer.

Both projects build warning-free with the analysers on. Nothing was suppressed.

**`[AotRecord]` was not taken.** It generates a `CreateRecordSet` extension and an `IdSelector`
of `record => record.Id.ToString()!`, which is what `CabinetStore` already writes by hand in one
line. Taking it would put a Cabinet attribute on `Envelope`, which lives in `Hyperwyc.Core` —
the project that deliberately has no Cabinet dependency. Rejected on that, not on taste.

## Acceptance Criteria

- [x] An internal `JsonSerializerContext` in `Hyperwyc` covering the full `List<Envelope>` graph.
- [x] `CabinetStore` passes `new JsonSerializerOptions { TypeInfoResolver = <context>.Default }`
      through the four-argument `FileOfflineStore` constructor.
- [x] `IsAotCompatible` set on both src projects, so the compiler tells us next time rather than
      a consumer's release build.
- [x] Build is warning-free with the analyzers on. One real finding, fixed rather than suppressed.
- [x] `[AotRecord]` considered and declined — it would put a Cabinet attribute on a
      `Hyperwyc.Core` type.

## How it is held

`Hyperwyc.Tests` sets `JsonSerializerIsReflectionEnabledByDefault=false`, so System.Text.Json's
reflection fallback is off for that test host and a store not wired to the context throws
`NotSupportedException`.

That property is doing the real work here. Without it the two new round-trip tests
(`FullEnvelopeGraph_SurvivesReopeningTheStore`, `DeadLetterFlag_SurvivesReopeningTheStore`) pass
whether the context is wired up or not, because reflection works perfectly well under a JIT test
runner — which is precisely the shape of bug this item is about. With it, reverting the
`CabinetStore` change fails 22 of the 35 tests in that project. Verified, not assumed.

The round-trip tests reopen the store over the same directory rather than reading back through
the same instance: `RecordSet` holds an in-memory copy, so a same-instance assertion proves the
write and says nothing about the read. They also populate every branch of the graph, because a
type missing from a context comes back as a null or a default rather than as an exception.

## Notes

- Landed for **v1.0**, as proposed.
- **On-disk format is unchanged in practice.** Compatibility was declared a non-requirement, so
  nothing was spent preserving it — but the context uses no naming policy, matching Cabinet's
  reflection default, so an existing store still reads. Incidental, not a guarantee.
- The docs lost their AOT caveats rather than gaining a section: the bullet in
  `choosing.md`'s **Current limitations**, and the second of `storage.md`'s "two things to know
  before you ship", which is now one thing. `maui.md`'s step 5 went from a warning to "nothing to
  do", with the one AOT decision that *is* still the reader's — a context for their own models
  through `GetFromJsonAsync` — named as unchanged by Hyperwyc either way.
- Cabinet's README warns that record types and the context need compatible accessibility. Our
  models are public, so an internal context is fine; the rule bites the other way round
  (internal records, public context).
