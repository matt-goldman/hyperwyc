# Issue 13 — Connectivity: Test Double in Core, MAUI as a Reference Implementation

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

- [ ] `StaticConnectivityService` implemented in the core package with XML doc comments.
- [ ] Unit tests cover: initial state, state transition, observer notification, multiple observers.
- [ ] MAUI reference implementation included in the POC app (issue #19).
- [ ] README documents the MAUI implementation as copy-and-paste reference code.
- [ ] README states that non-MAUI consumers implement the two-member interface directly, and
      that `AlwaysOnlineConnectivityService` is the default when none is supplied.

## Notes

- **Revisit after the POC.** If the reference implementation proves to have real gotchas —
  background-thread marshalling, `ConstrainedInternet` handling, Android's unreliable
  reporting — that is the evidence for a first-party `Hyperwyc.Maui` package. Shipping it now
  would mean versioning and supporting twenty lines that have not yet run in a real app.
- `Hyperwyc.Maui` is preferred over `Plugin.Maui.Hyperwyc` if that package is ever created, to
  match the provider-package naming already established by the storage providers.
- The existing `Fakes/FakeConnectivityService` in `Hyperwyc.Tests` can likely be replaced by
  `StaticConnectivityService` once it ships.
