# Issue 31 — Package Structure: `Hyperwyc` (batteries included) over `Hyperwyc.Core`

## Summary

Rename the existing two projects so that the obvious package is the one that works:
`Hyperwyc.Core` holds today's core, and `Hyperwyc` holds today's Cabinet provider plus an
`AddHyperwyc()` that defaults the store to Cabinet. `dotnet add package Hyperwyc` followed by
`AddHyperwyc()` with no further configuration gives a working, durable offline layer.

## Background

Today `dotnet add package Hyperwyc` installs the core only. `HyperwycOptions.Store` defaults to
`InMemorySyncStore`, so the obvious install produces an offline-persistence library that does
not persist — and it fails silently, at app restart, which is precisely the moment the library
exists to handle. Durable storage requires knowing to install a second package.

The guiding requirement: **out of the box, the library should work.** A consumer should not
have to make additional decisions or wire anything up to get the behaviour the README promises.

## Structure

| Package | Assembly | Namespace | Contents |
|---|---|---|---|
| `Hyperwyc.Core` | `Hyperwyc.Core.dll` | `Hyperwyc` | Today's `src/Hyperwyc` project unchanged: handler, orchestrator, interfaces, `InMemorySyncStore`, `AlwaysOnlineConnectivityService`. No storage dependency |
| `Hyperwyc` | `Hyperwyc.dll` | `Hyperwyc.Cabinet` | Today's `src/Hyperwyc.Cabinet` project, plus the batteries-included `AddHyperwyc()`. Depends on `Hyperwyc.Core` and `Cabinet` |

Package ID, assembly name and root namespace are independent, so **the namespaces do not
change** and no consumer source churn is involved. This is a project/package rename plus one
new extension method.

### Entry points

`Hyperwyc.Core` and `Hyperwyc` cannot both expose `AddHyperwyc` in scope at once — consumers of
the batteries package reference Core transitively, so two identically-named extension methods
on `IServiceCollection` produce a CS0121 ambiguity. The names must differ:

- `Hyperwyc.Core` — `AddHyperwycCore<TStore>()`. **Requires** a store; see the API design below.
- `Hyperwyc` — `AddHyperwyc(this IServiceCollection, Action<HyperwycOptions>?)`. Supplies
  `CabinetSyncStore` and delegates to `AddHyperwycCore`. The documented entry point for the
  common case.

## API design — the store is a type parameter

`AddHyperwycCore` requires an explicit store. `InMemorySyncStore` is for testing; the
zero-configuration path that "just works" is `AddHyperwyc` in the batteries package. A consumer
reaching for Core is doing so deliberately, because they are supplying their own `ISyncStore`.

The store is a **generic type parameter**, not an options property:

```csharp
// Hyperwyc.Core
public static IServiceCollection AddHyperwycCore<TStore>(
    this IServiceCollection services,
    Action<HyperwycOptions>? configure = null)
    where TStore : class, ISyncStore;

// Escape hatch: stores the container cannot construct, or a pre-built instance
public static IServiceCollection AddHyperwycCore(
    this IServiceCollection services,
    Func<IServiceProvider, ISyncStore> storeFactory,
    Action<HyperwycOptions>? configure = null);
```

The type parameter is enforced by the compiler, so a missing store cannot be expressed. A
runtime `throw` when `options.Store` is unset was rejected: it still permits shipping an app
with a forgotten store and discovering it in production.

### Consequences

1. **`HyperwycOptions.Store` is removed.** If a store can be given as a type parameter *and* set
   on options, there are two ways to specify one, a precedence rule to document, and no
   compile-time guarantee — the type parameter could be satisfied and then overridden by an
   instance. The type parameter must be the only way in.

2. **Store implementations need a DI-resolvable constructor.** `CabinetSyncStore(string dbDirectory)`
   and `CabinetSyncStore(string, byte[])` cannot be built by the container, which has no way to
   inject a bare `string`. Left as-is, `AddHyperwycCore<CabinetSyncStore>()` would compile and
   then throw at resolution — recreating the failure class this design removes, moved later and
   made harder to spot. Configuration must flow through an options object, as
   `AddDbContext<TContext>` does. The existing positional constructors stay for direct
   construction and for the factory overload.

3. **Storage-specific configuration stays out of `HyperwycOptions`.** Cabinet's path and
   encryption key belong to a `CabinetStoreOptions` in the batteries package. A `StorePath` on
   the core options type would leak a storage concept across the pluggability boundary — an
   IndexedDb store has no path.

4. **Disposal ownership becomes correct.** The current registration,
   `TryAddSingleton<ISyncStore>(_ => options.Store)`, is a factory closing over a
   consumer-created instance; because the container invoked the factory it treats itself as the
   owner and will dispose it, though the consumer constructed it and may still hold the
   reference. No `ISyncStore` implements `IDisposable` today so the problem is latent, but a
   store holding file handles or a connection would hit it. Container construction makes
   ownership unambiguous.

## Decision — why not a single merged package

Merging Cabinet into the core was considered and rejected:

- **It destroys a boundary that already exists.** The two projects are already separate. Merging
  them, then re-splitting once `Hyperwyc.IndexedDb` or `Hyperwyc.Sqlite` lands, is work in both
  directions to arrive back where the repo already is.
- **The later re-split is binary-breaking.** Packages `Hyperwyc` and `Hyperwyc.Core` cannot both
  ship `Hyperwyc.dll`, so extracting the core later renames its assembly and moves types across
  an assembly boundary. Type forwarders can cover it, but it is a migration best not created.
- **It works against a stated direction.** Storage pluggability is principle 4 in
  TECHNICAL_PLAN, and the roadmap lists IndexedDb, LiteDb and Sqlite providers. A merged core
  also forces Cabinet's IL onto Blazor WASM consumers who would want IndexedDb — and because
  `AddHyperwyc` would reference `CabinetSyncStore` as its default, the trimmer cannot prove it
  dead, so it genuinely ships.
- **It would require rewording principle 4.** Under the structure above that principle stays
  true as written; it simply describes `Hyperwyc.Core`.

An *empty* meta package over Core + Cabinet was also rejected, for a different reason: it cannot
deliver zero-config defaults. `AddHyperwyc` must be compiled against Cabinet to default the
store to it, and a dependency-only package contains no code to do that. Reflection probing is
AOT- and trim-hostile on MAUI mobile, and a `[ModuleInitializer]` in the Cabinet assembly is
unreliable because assembly loading is lazy. The structure above avoids this entirely: the
assembly defining `AddHyperwyc` references Cabinet directly.

Cabinet is cheap to depend on regardless — **version 1.0.7 has zero transitive dependencies**
and is pure managed code, a smaller footprint than the Polly reference the core already carries.

## Resolved during implementation

1. **Shape of `CabinetStoreOptions`.** `AddHyperwyc` takes a second optional delegate,
   `Action<CabinetStoreOptions>?`, carrying `DirectoryPath` and `EncryptionKey`. The options
   object is registered as a singleton and injected into `CabinetSyncStore`'s new constructor.
2. **Consistency of the other pluggables.** `Connectivity`, `StalenessEvaluator` and
   `DefaultPolicy` stay as instances on `HyperwycOptions`. All three have working defaults, so
   none needs the compile-time enforcement a store does, and moving them would churn the API
   for no behavioural gain. Recorded as a deliberate asymmetry rather than an oversight.

## Decided

- **Default store location:** `Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)`.
  **Since confirmed on a real Android device** by the sample ([issue 19](../19-poc-maui-app.md)):
  a catalogue cached before the app was killed was still readable after a restart with the
  network disabled, which requires the path to be both writable and stable across process
  lifetimes — and incidentally proves the path-derived encryption key round-trips too. iOS
  remains reasoned rather than observed.
  `FileSystem.AppDataDirectory` is rejected — it is MAUI Essentials, for the reasons in issue #13.
- **No `InMemorySyncStore` fallback in Core.** `AddHyperwycCore` requires a store. `InMemorySyncStore`
  stays public and supported, for tests and for consumers who explicitly want non-durable behaviour.
- **The default store is constructed by the container**, so it is never built when the caller
  supplies their own and no filesystem access occurs at options-construction time. This avoids
  the construction-order trap that produced issue #29.

## Acceptance Criteria

- [x] `src/Hyperwyc` renamed to `Hyperwyc.Core` (package, assembly and project); namespace `Hyperwyc` retained.
- [x] `src/Hyperwyc.Cabinet` renamed to `Hyperwyc` (package, assembly and project); namespace `Hyperwyc.Cabinet` retained.
- [x] Test projects renamed to match and passing.
- [x] `AddHyperwycCore<TStore>()` and the factory overload in Core; `AddHyperwyc` in the batteries package delegating to it.
- [x] `HyperwycOptions.Store` removed; the store cannot be specified two ways.
- [x] `CabinetSyncStore` gains a DI-resolvable constructor taking `CabinetStoreOptions`; the existing positional constructors are retained.
- [x] `AddHyperwyc()` with no configuration resolves a durable `CabinetSyncStore` rooted at `LocalApplicationData`.
- [x] The default store is not constructed, and no filesystem access occurs, when the caller supplies their own.
- [x] Registering a custom store via `AddHyperwycCore<TStore>()` and via the factory overload are both covered by tests.
- [x] `PackageId`, `Version`, description and README metadata set in `Directory.Build.props` — none currently exist.
- [x] Package descriptions distinguish the two clearly, so nobody installs Core expecting batteries.
- [x] README quick start updated: one package, no store wiring, with a short note on when to use Core.
- [x] TECHNICAL_PLAN package structure table updated once the code lands.

## Notes

- Nothing is published to NuGet and there is no publish workflow, so this restructure has no
  consumer impact if done before the first release.
- Precedent for the shape: `Polly.Core` is the modern core and `Polly` is a real assembly that
  depends on it and adds a broader surface; `xunit` aggregates `xunit.core` and friends.
- The solution file `hyperwyc.slnx` and the CI workflow reference project paths and will need
  updating alongside the rename.
- An earlier draft of this item recommended a single merged package. That recommendation
  understated two things: the boundary being merged already exists in source, and the later
  re-split moves types across an assembly boundary rather than being cleanly non-breaking.
