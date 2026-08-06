# Issue 37 — Replays Must Go Through the Pipeline, Skipping Only Our Handler

## Summary

`SyncOrchestrator` replays queued writes through a bare transport, bypassing the application's
entire `HttpClient` pipeline. It should instead send through the *same* pipeline, with
`HyperwycHandler` passing replayed requests straight through rather than intercepting them.

## The defect

Handlers run outermost-first, so with the ordering the README documents:

```csharp
.AddHttpMessageHandler<HyperwycHandler>()   // outermost — sees the request first
.AddHttpMessageHandler<AuthHandler>()       // inner — adds the token
```

`HyperwycHandler` serialises the request into an envelope **before** `AuthHandler` has run, so
the envelope captures no `Authorization` header. The orchestrator then replays it through a
bare transport that has no auth handler either. The request is sent unauthenticated, is
rejected, exhausts its retry budget, and is dead-lettered.

Reversing the order does not fix it: the envelope then captures the token that was valid when
the request was *queued*, which is stale by the time connectivity returns — the exact problem
the documented ordering exists to avoid.

**So offline writes do not currently work against an authenticated API in either
configuration.** That is the library's headline feature against the majority of real APIs.

The documentation has described the intended behaviour all along — README "Auth Handler
Placement" and TECHNICAL_PLAN §1 both state that replays pick up fresh tokens. Only the
implementation diverged.

Auth is the sharpest case but not the only one. Logging, correlation-ID stamping, custom
telemetry and retry handlers configured on the client all miss replayed traffic today.

## Design

### 1. `HyperwycHandler` passes replays through

The orchestrator marks a replayed request; the handler checks for the marker first and
delegates straight to `base.SendAsync`, skipping queueing, caching, invalidation and events.

```csharp
internal static readonly HttpRequestOptionsKey<bool> ReplayMarker = new("Hyperwyc.Replay");
```

A marker is required rather than inferring it — a replayed write reaching the normal online
path would publish a second `OnSynced`, re-run cache invalidation, and leave the orchestrator
unable to tell whether it or the handler owned the outcome.

### 2. The orchestrator sends through the originating client

It resolves `IHttpClientFactory.CreateClient(name)` rather than holding its own transport. This
adds a `Microsoft.Extensions.Http` dependency to `Hyperwyc.Core`, which is reasonable: any
consumer calling `AddHttpClient(...)` already has it.

### 3. Envelopes record which client they came from

An application may have several named clients using Hyperwyc — `OrdersApi` and `ProfileApi`
with different base addresses and auth — so a replay must go back through the one it came from.
`Envelope` gains a client name, which means a change to the persisted shape.

### 4. The handler learns its client name at registration

`IHttpClientBuilder` exposes `Name`, so a Hyperwyc-supplied extension can capture it with no
duplication by the consumer:

```csharp
services.AddHttpClient("MyApi")
    .AddHyperwycHandler()               // captures "MyApi"
    .AddHttpMessageHandler<AuthHandler>();
```

This replaces `.AddHttpMessageHandler<HyperwycHandler>()` as the documented registration. The
plain form can keep working with no client name, falling back to `ReplayTransport` — see the
open question.

## Consequences

- **Breaking change to the persisted envelope shape.** Best landed together with issue #25
  (binary bodies), which is also a persisted-shape change and also pre-1.0, so one migration
  conversation covers both.
- **Registration API change**, from `AddHttpMessageHandler<HyperwycHandler>()` to
  `AddHyperwycHandler()`. Cheap now, and the demo (issues #18, #19) should be built against the
  new form rather than encoding the old one.
- **Resolves the hard half of issue #30.** That item had to answer "how do replayed requests
  acquire credentials if we stop persisting `Authorization`?" — the answer is this issue.
  Excluding sensitive headers becomes straightforwardly correct instead of breaking replay.
- **`HyperwycOptions.ReplayTransport` (issue #35) is largely superseded**, though see below.

## Open Questions

1. **Does `ReplayTransport` survive?** It remains the answer for a consumer not using
   `IHttpClientFactory` at all, and for tests that want a stub without standing up a client
   factory. Options: keep it as the fallback when an envelope has no client name; keep it as an
   override that wins outright; or remove it and require the factory. Leaning toward the first —
   it degrades gracefully and keeps the test story simple.
2. **Envelopes queued before an upgrade** have no client name. Pre-1.0 the answer is a store
   reset, consistent with the decision recorded in issue #25.
3. **Does the marker need to survive a `HttpRequestMessage` rebuild?** `BuildRequest` constructs
   a fresh message per attempt, so the marker must be applied there rather than assumed to
   persist across retries.

## Acceptance Criteria

- [x] `HyperwycHandler` passes marked replays straight to `base.SendAsync`, with no queueing,
      caching, invalidation or event publication.
- [x] The orchestrator replays through the originating named client's full pipeline.
- [x] `Envelope` records the originating client name.
- [x] `AddHyperwycHandler()` extension captures the client name from `IHttpClientBuilder.Name`.
- [x] Decision recorded on the fate of `ReplayTransport`.
- [x] Unit test: a replayed request passes through a downstream handler registered after
      `HyperwycHandler` — the auth case, proven rather than assumed.
- [x] Unit test: a replay does not publish a duplicate `OnSynced` or re-run cache invalidation.
- [x] Unit test: with two named clients, each envelope replays through its own pipeline.
- [x] Unit test: a replayed request carries a token applied by a downstream auth handler at
      replay time, not one captured at queue time.
- [x] README "Auth Handler Placement" and TECHNICAL_PLAN §1 updated — they currently describe
      this design as though it were implemented.

## Resolution

`HttpRequestOptionsKey<bool>` marker set per attempt in `BuildRequest` (open question 3 —
`BuildRequest` constructs a fresh message per retry, so the marker could not be set once and
assumed to persist). `HyperwycHandler.SendAsync` checks it first and delegates to
`base.SendAsync`.

`SyncOrchestrator` takes an optional `IHttpClientFactory` and resolves
`CreateClient(envelope.ClientName)` per envelope; `Microsoft.Extensions.Http` was added to
`Hyperwyc.Core`. `Envelope.ClientName` is captured by `AddHyperwycHandler()` from
`IHttpClientBuilder.Name`.

**Open question 1 resolved: `ReplayTransport` survives as the fallback.** It applies when an
envelope has no client name — a handler registered through the plain
`AddHttpMessageHandler<HyperwycHandler>()` form, or one constructed by hand outside DI. That
keeps the older registration working rather than breaking it, and keeps a simple story for
tests that do not want to stand up a client factory.

### Verified rather than assumed

The regression test queues a write while offline with an auth handler registered *after*
Hyperwyc, asserts the envelope captured **no** `Authorization` header, rotates the token,
restores connectivity, and asserts the replayed request carried the *new* token. That test
fails against the previous implementation, which is what makes it worth having.

A second test covers a subtlety the design notes anticipated: if connectivity still reports
offline while a flush runs, the replay must not be queued a second time. Without the marker it
would be, silently duplicating the envelope.

## Notes

- Raised in review of issue #35's replay transport: the right question was not "which transport
  should replays use" but "why are replays leaving the pipeline at all".
- Worth checking whether any other handler-level concern is silently skipped on the offline
  path. Handlers after `HyperwycHandler` are also not invoked when a synthetic offline response
  short-circuits the pipeline — that one is by design and documented, but the two behaviours
  are easy to conflate.
