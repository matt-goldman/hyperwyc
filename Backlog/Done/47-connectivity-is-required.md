# Issue 47 — Ship a BCL Connectivity Service, and Require One to Be Chosen

## Summary

Add `NetworkAvailabilityConnectivityService` to `Hyperwyc.Core` for consumers with no platform
implementation of their own, and make connectivity **required**: a consumer must either
register an `IConnectivityService` in the container — the expected route — or set
`HyperwycOptions.Connectivity`. Doing neither throws.

## Status

**Done.** Both halves shipped together — one without the other is worse than neither.

## The problem

`Connectivity` defaulted to `AlwaysOnlineConnectivityService`. Two things were wrong with that.

**The default fails silently.** Under it, `IsConnected` is always `true`, so every request
takes the network path, nothing is ever queued and nothing is ever replayed. A consumer who
never reads the docs closely gets a library that caches responses and drops offline writes on
the floor, with no error, no log line and no failing test to say so. It looks like it works.
That is the precise failure mode Hyperwyc exists to prevent, arrived at by way of Hyperwyc's
own defaults.

**Non-MAUI consumers had nothing reasonable to reach for.** Issue #13 settled that the MAUI
implementation stays out of the core package — multi-targeting a platform-independent library
onto five TFMs for two members is a bad trade, and it stands. But that left a console, service
or desktop consumer with two options: write the interface themselves against a BCL API they
would have to find, or take the always-online default and quietly lose the queue.

## What shipped

### `NetworkAvailabilityConnectivityService`

BCL only, no platform dependency. `NetworkInterface.GetIsNetworkAvailable()` for state,
`NetworkChange.NetworkAvailabilityChanged` for the stream. It mirrors the four decisions the
sample's `MauiConnectivityService` arrived at under #13, because they were right there too:

- **No `System.Reactive`.** Hand-rolled `IObservable<bool>`, consistent with `SyncEventStream`.
- **A change stream, not a state view.** Nothing replays on subscribe; `IsConnected` answers
  "right now".
- **Only publish on an actual change.** Republishing "connected" triggers a flush with nothing
  new to send.
- **`IDisposable`, to unhook the platform event.** `NetworkChange` is a long-lived static, so a
  handler left attached keeps the service and its captures alive for the process lifetime.

Two things specific to this implementation:

- **Construction never fails.** Not every runtime supports change notifications — a browser
  host, for one. `PlatformNotSupportedException` and `NetworkInformationException` are
  swallowed when hooking the event, degrading the service to startup-and-manual flushing.
  That is a reduced service, not a broken one, and not worth failing construction over.
- **`SafeIsConnected` assumes connected on failure.** If the interfaces cannot be interrogated,
  letting the request through gets the truth from the transport, which is a better source than
  a guess.

The honest limitation, stated prominently in the XML docs and the README: **it reports whether
a network is available, not whether the API is reachable.** It catches aeroplane mode, Wi-Fi
off and an unplugged cable; it reports connected behind a captive portal, on a router with no
upstream, or on a signal too weak to carry a request. That costs a wasted attempt and some
latency, not correctness — `SyncOrchestrator` already abandons a flush on a transport failure
and waits for the next signal.

### Connectivity became required

`HyperwycOptions.Connectivity` is now `IConnectivityService?`, defaulting to `null`. When it is
unset, `AddCoreServices` registers a placeholder factory that throws an
`InvalidOperationException` naming all three implementations. The message leads with the
container registration, because that is what the fix normally is.

Shipping the BCL service without this change would have made things worse, not better: a third
option nobody has to notice, sitting behind a default that still silently does the wrong thing.

### The check is deferred, not made at registration

The first implementation threw from `AddCoreServices` when nothing had been supplied. That was
wrong, and the reason is worth keeping.

Registering `IConnectivityService` in the container is the normal way to satisfy this — not a
fallback for implementations with their own dependencies, which is how the first pass framed
it. Checking at registration time makes that route **order-dependent**: register after
`AddHyperwyc` and you get an exception telling you to do the thing you just did.

Worse, it forecloses on auto-wiring. A source generator that finds the single
`IConnectivityService` in an assembly and registers it has no control over where its output
lands relative to `AddHyperwyc`, so an order-dependent check would make Hyperwyc unusable with
one — for exactly the consumers who had done everything right.

So the placeholder throws on resolve instead. Both orders now work:

- registered **first**, `TryAddSingleton` stands aside and the consumer's descriptor is the
  only one;
- registered **later**, the consumer's descriptor is appended and wins, because a single-service
  resolve takes the last descriptor.

The cost is that a consumer who supplies nothing finds out on first use — resolving `IHyperwyc`,
`HyperwycHandler`, or the startup flush — rather than at registration. That is a real loss of
fail-fast, and it is the right trade: the error is unmissable whenever it lands, whereas
order-dependence is a silent trap for consumers who did nothing wrong.

## Why a required option, when everything else has a default

Both halves of this follow the same rule, and it is worth stating because it will come up
again.

Hyperwyc picks a store for the consumer (#31) because **any durable store will do** — the
choice is real but not load-bearing, and a wrong-but-working default is genuinely fine. It
cannot pick a connectivity source, because **the right answer depends on the platform**, which
is knowledge only the application has. Failing at startup is not friction for its own sake; it
is the only honest thing to do when the library cannot decide and the wrong decision is
invisible.

The general form: *default what you can decide correctly, require what you cannot.* Silence is
only acceptable when being wrong is loud. Recorded as
[ADR 0003](../../docs/decisions/0003-default-what-you-can-decide-correctly.md), since it will be
reached for again.

This is the same reasoning as the store being a type parameter rather than an option, and the
same reasoning that keeps the path-derived encryption key (#32) — there, a weak-but-working
default is better than nothing because the failure is visible in the threat model, not hidden
in the runtime behaviour.

## What it costs

The zero-configuration headline from #31 is narrower now. `AddHyperwyc()` with no arguments
throws; the shortest working registration is one line of options.

Accepted. "Works with no configuration" was only ever true in the sense that it *ran* — and
running while never syncing is the failure this library is for. The README now says storage
needs no decision and connectivity needs exactly one, which is both accurate and short.

## Acceptance criteria

- [x] `NetworkAvailabilityConnectivityService` in `Hyperwyc.Core`, no new dependencies.
- [x] `HyperwycOptions.Connectivity` is nullable with no default.
- [x] Resolving throws with a message naming all three options, leading with the container
      registration.
- [x] A container-registered `IConnectivityService` satisfies the requirement, registered
      before *or* after `AddHyperwyc`.
- [x] Tests: publishing rules, subscription lifetime, disposal, the throw at resolve, both
      registration orders, and precedence when both routes are used.
- [x] README gains a Connectivity section; Quick Start no longer claims zero configuration.
- [x] TECHNICAL_PLAN defaults table and type inventory updated.

## Notes

- `PublishIfChanged` is `internal` rather than private purely as a test seam:
  `NetworkAvailabilityEventArgs` has no accessible constructor, so the platform event cannot be
  raised from a test.
- **Deferring the check is what leaves room for a source generator.** An assembly declaring a
  single `IConnectivityService` implementation could have it registered automatically, putting
  this back to no configuration at all for the consumer. Not filed as an item; noted because
  the order-independence above is a precondition for it, and would be easy to undo by someone
  reinstating a registration-time check for the fail-fast.
- This does not reopen #13. The MAUI implementation still stays in the sample; the BCL service
  is the *fallback* for consumers who are not on MAUI, not a replacement for it.
