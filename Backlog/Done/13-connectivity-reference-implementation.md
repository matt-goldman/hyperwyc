# Issue 13 — Connectivity: Documentation, Not Code

## Summary

Ship `StaticConnectivityService` in the core package for testing, and document a MAUI
`IConnectivityService` implementation as copy-and-paste reference code in the POC and README.
Do **not** ship a `MauiConnectivityService` type in the core package.

## Decision

**Superseded scope.** This item originally proposed shipping `MauiConnectivityService` in
`Hyperwyc`, "behind a conditional compilation target (use `#if` or target framework
condition)". That approach is rejected.

Reaching `Connectivity.Current` requires MAUI Essentials, which means multi-targeting the
core as `net10.0;net10.0-android;net10.0-ios;net10.0-maccatalyst;net10.0-windows`. The
consequences are out of proportion to the two members involved:

- Anyone building the core from source needs `dotnet workload install maui`.
- CI runs on `ubuntu-latest`, which cannot build the iOS or macCatalyst targets at all — a
  macOS runner would be required to build a library whose entire premise is platform
  independence.
- Blazor WASM, ASP.NET Core and console consumers restore a package whose target framework
  list is dominated by mobile.

`IConnectivityService` is deliberately two members — `bool IsConnected` and
`IObservable<bool> ConnectivityChanged`. A MAUI implementation is roughly twenty lines, so
reference code carries nearly the same value as a shipped type at none of the structural cost.

Connectivity is also a *hint* rather than a source of truth in Hyperwyc's design: failure is
defined as a non-2xx response or timeout, not as a connectivity-state change, so a device
that reports connectivity but cannot reach the API is handled by the retry budget (see
TECHNICAL_PLAN §3). That further lowers the stakes of not shipping a canonical implementation.

## Behaviour

### `StaticConnectivityService` (core package)

A manually-controlled implementation for unit tests and for consumers who drive connectivity
from their own source.

- Constructor takes an initial `bool`.
- A method (e.g. `SetConnected(bool)`) updates `IsConnected` and pushes to `ConnectivityChanged`.
- Thread-safe; observers may be notified from any thread.

### MAUI reference implementation (documentation only)

Documented in the README and used by the POC app (issue #19):

- `IsConnected` returns `Connectivity.Current.NetworkAccess == NetworkAccess.Internet`.
- `ConnectivityChanged` wraps `Connectivity.Current.ConnectivityChanged` as `IObservable<bool>`.
- Must marshal correctly — MAUI raises connectivity events on a background thread.
- Should document the `NetworkAccess.ConstrainedInternet` case and the caller's choice about
  how to treat it.

## Acceptance Criteria

- [x] Decision recorded not to ship a test double from the core package — see below.
- [x] MAUI reference implementation included in the sample app (issue #19).
- [x] README documents the MAUI implementation as copy-and-paste reference code, carrying the
      decisions recorded under Progress.
- [x] README states what non-MAUI consumers should do — done by
      [issue 47](47-connectivity-is-required.md), which ships
      `NetworkAvailabilityConnectivityService` and documents all three choices. Supersedes the
      original wording, which described `AlwaysOnlineConnectivityService` as the default.
- [x] README shows how to control connectivity in a consumer's own tests — a hand-written fake
      of a few lines, rather than pointing at anything Hyperwyc ships.

## Second scope revision: no test double ships either

The remaining plan was to ship `StaticConnectivityService` from the core package for testing.
That is also rejected.

Run it through [the scope test](../../docs/decisions/README.md#the-standing-scope-test):

- Would the problem exist without Hyperwyc? Testing against an interface is a general problem.
- Does it require anything of the consumer's API? No.
- **Can the application already do it?** Yes, trivially — mock a two-member interface with any
  mocking library, or write five lines by hand.
- **Does it depend on something only Hyperwyc knows?** No.

Failing the last two is decisive. Beyond that, a test double shipped in the main package is
something a consumer can accidentally ship to production, and no name prevents it — `Static`
understates what it is, `Fake` and `Test` are clearer but equally referenceable. Where the
ecosystem does ship test doubles it puts them in a **separate testing package**:
`FakeTimeProvider` lives in `Microsoft.Extensions.TimeProvider.Testing`,
`System.IO.Abstractions.TestingHelpers` is its own package, and so on. That convention exists
precisely because the boundary needs to be visible in the dependency graph, not just the name.

A `Hyperwyc.Testing` package is therefore the only shape that would be defensible, and it is not
worth publishing and versioning a package for one twenty-line class that a consumer can mock.
Should there ever be enough testing surface to justify one — a fake store, a controllable clock,
a scriptable transport — that is when to revisit it.

**`AlwaysOnlineConnectivityService` is not a counter-example.** It is a functional
implementation, not a testing aid, and it is not there to be substituted in a test.

*Revised by [issue 47](47-connectivity-is-required.md):* it is no longer the default
either. Defaulting to it meant a consumer who supplied nothing got a library that cached but
never queued or replayed — silently. Connectivity is now required, and #47 also ships
`NetworkAvailabilityConnectivityService` so that a non-MAUI consumer has something to reach
for that is not "always connected".

The remaining work here is documentation, which was going to be needed regardless.

## Progress

`MauiConnectivityService` exists in the sample and is proven on an Android device: with Wi-Fi
and mobile data disabled it reports disconnected, which is what routes a read down
`HandleOfflineReadAsync` and serves the cached catalogue. Without it the same test would have
passed through the *online* path finding a fresh entry, which proves considerably less.

Four decisions taken while refining it, all of which should carry into the README write-up
because that is what people will copy:

- **No `System.Reactive`.** The first version used a `BehaviorSubject<bool>`, which meant the
  package reference existed solely for one field. Hand-rolling the observable mirrors how the
  core implements `IObservable<T>` and keeps the reference implementation from imposing a
  dependency Hyperwyc deliberately avoids. The sample no longer references `System.Reactive` at
  all.
- **A change stream, not a state view.** Nothing is replayed on subscribe;
  `IConnectivityService.IsConnected` answers "right now". This matches
  `AlwaysOnlineConnectivityService`, whose observable emits nothing at all, and it dissolves an
  inconsistency the first version had, where the subject said `false` at startup regardless of
  actual connectivity while `IsConnected` read live state.
- **Only publish on an actual change.** The platform raises `ConnectivityChanged` for any change
  in network access, including moving between Wi-Fi and cellular while remaining online.
  Forwarding that as a connectivity restoration triggers a flush for nothing, so the service
  compares against the last published value — seeded from live state — and stays quiet otherwise.
- **`IDisposable`, to unhook the platform event.** `Connectivity.Current` is a long-lived static,
  so a handler left attached keeps the service and everything it captures alive for the process
  lifetime. Irrelevant for an app-lifetime singleton, but reference code gets copied into places
  where it is not. Note `IConnectivityService` itself is not `IDisposable`; the consumer supplies
  the instance and therefore owns it, consistent with `ReplayTransport` ownership.

`NetworkAccess.ConstrainedInternet` counts as disconnected, which is the conservative reading
this item called for, and is documented in the code as a deliberate choice.

## Closed

Everything remaining was documentation, and it is done. Two places, because they serve different
readers:

- **README, under [Connectivity](../../docs/connectivity.md).** The full implementation in a
  collapsible block, followed by the five things in it that are deliberate — no
  `System.Reactive`, change-stream rather than state-view, publish-only-on-change, `IDisposable`
  to unhook the static, and no thread marshalling. Plus the `ConstrainedInternet` reading and how
  to change it. Then a section on faking connectivity in a consumer's own tests, which is the
  other half of not shipping a test double: declining to ship one is only reasonable if writing
  one is obviously easy, so the README shows it rather than asserting it.
- **The sample source itself**, which is what people actually copy. The same reasoning as
  comments and XML docs, so it travels with the code rather than being left behind in a README
  nobody re-reads. The two are kept identical: the README block and the sample file differ only
  in comments.

The testing guidance leads with the observation that most tests never need the change stream at
all, because they drive sync with `FlushAsync()` rather than waiting for an event. That reduces
the fake to a settable `bool` and a never-emitting observable, which is the strongest form of the
argument for not shipping one.

## What changed about this item along the way

Worth keeping, because the item ended up somewhere quite different from where it started.

1. **Originally:** ship `MauiConnectivityService` from the core package behind a conditional
   compilation target. Rejected — five TFMs, a MAUI workload to build from source, and no
   `ubuntu-latest` CI for a platform-independent library.
2. **Then:** ship `StaticConnectivityService` as a test double. Rejected — fails the scope test
   on questions 3 and 4, and test doubles belong in a separate testing package if anywhere.
3. **Then:** documentation only, with `AlwaysOnlineConnectivityService` as the default when
   nothing is supplied. Revised by [issue 47](47-connectivity-is-required.md): that default
   failed silently, so connectivity became required and a BCL implementation shipped instead.
4. **Now:** documentation only, with three named implementations and no default.

Each step removed something from the package. That is the direction this codebase should
generally travel, and this item is the clearest example of it.

## Notes

- **Revisit after the POC.** If the reference implementation proves to have real gotchas —
  background-thread marshalling, `ConstrainedInternet` handling, Android's unreliable
  reporting — that is the evidence for a first-party `Hyperwyc.Maui` package. Shipping it now
  would mean versioning and supporting twenty lines that have not yet run in a real app.
- `Hyperwyc.Maui` is preferred over `Plugin.Maui.Hyperwyc` if that package is ever created, to
  match the provider-package naming already established by the storage providers.
- The existing `Fakes/FakeConnectivityService` in `Hyperwyc.Tests` stays. Nothing ships from
  core to replace it — see the second scope revision above.
