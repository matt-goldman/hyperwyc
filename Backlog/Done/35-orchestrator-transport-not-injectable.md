# Issue 35 — The Orchestrator's Replay Transport Is Not Injectable

## Summary

`AddCoreServices` hardcodes `new HttpClientHandler()` as the transport `SyncOrchestrator` uses
to replay queued writes. There is no way to supply a different one, which makes integration
testing hit the real network and blocks legitimate transport customisation.

## Background

```csharp
services.TryAddSingleton<SyncOrchestrator>(sp => new SyncOrchestrator(
    ...,
    new HttpClientHandler()));          // ServiceCollectionExtensions.cs
```

Discovered when a test that resolved `IHyperwyc` from the container and called `FlushAsync`
made a real HTTP request to `example.com` and took **29 seconds** to exhaust the default retry
budget — turning a 0.5-second suite into a 29-second one. Worse, the test passed for the wrong
reason: the envelope was dead-lettered, which also empties the pending outbox, so the assertion
held while nothing had actually been delivered.

The test was rewritten to construct the orchestrator directly with a stub transport. That works
for this repo's own tests, but a *consumer* has no such option: there is no supported way to
exercise their Hyperwyc integration without real network calls.

## Why it matters beyond testing

The replay transport is a legitimate customisation point:

- **Certificate pinning** on mobile, commonly applied at the handler level.
- **Proxy configuration**, corporate or otherwise.
- **Timeouts and connection limits** differing from the app's foreground `HttpClient`.
- **Diagnostics** — a logging handler wrapping replay traffic specifically.

Right now none of these can reach replayed requests, even though they apply to the app's
ordinary requests through its own `HttpClient` pipeline. That is a surprising asymmetry: a
request sent while online honours the app's handler stack, and the *same request* replayed from
the outbox does not.

It also interacts with issue #30: the orchestrator replays through a transport with no auth
handler, so a persisted `Authorization` header is what actually goes on the wire. Any fix there
needs a way to give the orchestrator an auth-capable transport — which is this issue.

## Behaviour

Allow the transport to be supplied, defaulting to today's behaviour:

- A `HyperwycOptions` member — an `HttpMessageHandler`, or a factory, or a named
  `IHttpClientFactory` client to resolve. Decide during implementation; the factory form avoids
  the ownership question below, and the `IHttpClientFactory` form composes best with how
  consumers already configure handler pipelines.
- Default remains a `HttpClientHandler` so existing behaviour is unchanged.
- Whatever is chosen must make it straightforward for a consumer to substitute a stub in tests.

## Open Question

**Ownership and disposal.** `SyncOrchestrator` constructs its `HttpMessageInvoker` with
`disposeHandler: false`, and nothing disposes the `HttpClientHandler` that `AddCoreServices`
creates — it is an application-lifetime singleton that dies with the process, so untidy rather
than harmful. Once the transport is caller-supplied, ownership needs stating: the caller's
handler must not be disposed by Hyperwyc, while a Hyperwyc-created default arguably should be.
Resolving the default through DI rather than `new`-ing it would hand the question to the
container.

## Acceptance Criteria

- [x] The orchestrator's transport can be supplied through `HyperwycOptions`.
- [x] The default is unchanged for callers who supply nothing.
- [x] A consumer can flush against a stub transport with no network access, and this is
      documented.
- [x] Ownership and disposal of a caller-supplied transport is defined and documented.
- [x] Unit test: a supplied transport receives replayed requests.
- [x] Unit test: a supplied transport is not disposed by Hyperwyc.
- [x] `ServiceCollectionExtensionsTests.IHyperwyc_FlushAsync_DrainsTheOutbox` reverted to
      resolving `IHyperwyc` from the container, and its explanatory comment removed.

## Resolution

`HyperwycOptions.ReplayTransport` — an `HttpMessageHandler?` defaulting to `null`, in which
case a plain `HttpClientHandler` is used exactly as before.

**Chosen over the factory and `IHttpClientFactory` forms.** An instance is consistent with the
other pluggables on this type (`Connectivity`, `StalenessEvaluator`), and the
`IHttpClientFactory` form would have added a `Microsoft.Extensions.Http` dependency to a core
package that currently has three — too much to pay before a consumer has asked for it. The
option can be widened later without breaking anyone.

**Ownership resolved as: Hyperwyc never disposes the transport**, supplied or defaulted. This is
what the code already did — `SyncOrchestrator` constructs its `HttpMessageInvoker` with
`disposeHandler: false` — so the resolution documents an existing decision rather than changing
behaviour. Tracking ownership so the default could be disposed was considered and rejected: it
would have required disposing under a possibly-unwinding flush, which is exactly the hazard
issue #33 removed, in exchange for reclaiming a socket pool microseconds before process exit.

The undisposed default remains a real if minor untidiness. A consumer who cares now has the
means to take control of it, which is the meaningful improvement.

## Notes

- Filed while making `SyncOrchestrator` internal (issue #35's sibling work on public surface).
  The hardcoded transport was pre-existing; internalising the orchestrator simply removed the
  last workaround, since consumers can no longer construct one with their own transport.
- Related: issue #30, which cannot be fully resolved without this.
- Worth adding a guard so this class of mistake is caught rather than discovered by a slow
  suite: no unit test should reach the network. A sharply lower default retry budget in tests,
  or a check on suite duration, would both surface it.
