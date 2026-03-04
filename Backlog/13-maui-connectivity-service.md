# Issue 13 — `IConnectivityService` — Default MAUI Essentials Implementation

## Summary

Provide `MauiConnectivityService`, the default `IConnectivityService` implementation that wraps .NET MAUI Essentials' `Connectivity` API.

## Background

`IConnectivityService` is a pluggable interface defined in the `Restyc` core. The MAUI-specific implementation belongs in `Restyc` itself (behind a conditional compilation target) or in a thin helper — to be decided during implementation. The goal is that MAUI developers get a working connectivity service with minimal wiring.

## Behaviour

- `IsConnected`: returns `true` when `Connectivity.Current.NetworkAccess == NetworkAccess.Internet`.
- `ConnectivityChanged`: wraps `Connectivity.Current.ConnectivityChanged` as an `IObservable<bool>`.
- Thread-safe observable — connectivity events may arrive on a background thread.

## Acceptance Criteria

- [ ] `MauiConnectivityService : IConnectivityService` implemented.
- [ ] `IsConnected` uses `Connectivity.Current.NetworkAccess`.
- [ ] `ConnectivityChanged` observable raises `true`/`false` matching MAUI's connectivity events.
- [ ] Class is only compiled when the MAUI target framework is active (use `#if` or target framework condition).
- [ ] `MauiConnectivityService` is registered automatically when `AddRestyc()` detects a MAUI context, or documented as a manual registration step.
- [ ] Unit-testable alternative: `StaticConnectivityService` (takes a constructor bool + manual raise) is provided for tests.
- [ ] XML doc comments on all public members.

## Notes

- For non-MAUI targets (ASP.NET Core, console), developers implement `IConnectivityService` themselves or use a provided `AlwaysOnlineConnectivityService` stub.
- `StaticConnectivityService` for testing can live in `Restyc` directly; it is useful beyond just this issue.
