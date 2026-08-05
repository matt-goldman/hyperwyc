# Issue 33 — `SyncOrchestrator` Cannot Be Disposed Synchronously

## Summary

`SyncOrchestrator` implements `IAsyncDisposable` but not `IDisposable`. Once it has been
resolved from the container, disposing the service provider synchronously throws
`InvalidOperationException`.

## Background

Surfaced by a new registration test while doing issue #31, but the defect predates it — the
orchestrator has been registered as a singleton by the DI extension since it was introduced,
and no existing test disposed a provider that had resolved it.

```
System.InvalidOperationException : 'Hyperwyc.SyncOrchestrator' type only implements
IAsyncDisposable. Use DisposeAsync to dispose the container.
```

Microsoft.Extensions.DependencyInjection refuses to dispose an `IAsyncDisposable`-only
singleton from the synchronous `ServiceProvider.Dispose()` path, by design. Any consumer who
writes `using var host = builder.Build();` rather than `await using`, or who disposes a scope
or provider synchronously, hits this at shutdown — after the app has apparently worked fine
for its whole run.

## Decision

`SyncOrchestrator` implements both `IDisposable` and `IAsyncDisposable`. Both mean the same
thing: **stop now.** Signal cancellation, let the in-flight flush
exit at its next envelope boundary, tear down cleanly. Neither waits for a flush to complete.

| Path | Difference |
|---|---|
| `Dispose()` | Cancels and returns; teardown must tolerate a flush still unwinding. |
| `DisposeAsync()` | Cancels and awaits the flush's acknowledgment of cancellation, so teardown is not racy. |

The difference is teardown hygiene, not policy. A developer writing `using` rather than
`await using` is not choosing different behaviour for their user, and does not need to
understand a trade-off to pick correctly.

### Why abandoning is cheap

Durability comes from the outbox, not from shutdown behaviour. An abandoned flush is not lost
work — the envelopes are still queued, `FlushOnStartup` defaults to `true`, and they replay at
next launch.

Two things make that sufficient. First, **disposal frequently does not run at all**: Android
and iOS suspend and then terminate processes without invoking `Dispose`, `DisposeAsync` or
finalizers, so no design that depends on disposal can be load-bearing for a user's data.
Second, issue #34 establishes that **shutdown is not a flush trigger on any platform** —
envelopes are queued only because connectivity was poor, and shutting down does not improve
connectivity.

Together those reduce this issue to **teardown correctness**: do not crash, do not leak an
unobserved exception, release what was acquired. Data safety is answered by the outbox and the
existing flush triggers, not here.

## Behaviour

### 1. Stop waiting for the flush

`DisposeAsync` currently waits on the flush gate with no timeout and no cancellation token:

```csharp
await _flushGate.WaitAsync().ConfigureAwait(false);   // SyncOrchestrator.cs:260
```

With the default retry budget — 5 attempts at 2s initial delay and ×2 backoff — a single
envelope can hold that gate for roughly a minute, and a flush drains the outbox sequentially,
so this can hang shutdown for minutes.

Given issue #34's decision that shutdown is not a flush trigger, this wait is not merely
unbounded but unwanted: there is no reason to delay teardown for work that will be retried at
next launch anyway. Replace it with cancellation plus an await on the flush task's
acknowledgment, which completes at the next envelope boundary.

**No disposal-timeout option is added to `HyperwycOptions`.** An earlier draft proposed one;
it is unnecessary once neither path waits for flush completion.

### 2. Introduce a lifetime cancellation token

Both paths need to signal the in-flight flush rather than tear its primitives away. Today
`Dispose` cancels `_debounceCts`, which reaches connectivity-triggered flushes only — a manual
`FlushAsync()` from a "sync now" button passes `default` and **cannot be cancelled by disposal
at all**.

Give the orchestrator a lifetime `CancellationTokenSource`, link every flush to it, and cancel
it on disposal. `FlushAsync` already checks `ct.IsCancellationRequested` between envelopes, so
the loop exits cleanly at the next boundary.

### 3. Do not dispose primitives under a running flush

This is the trap in "just don't wait". `FlushAsync` releases the gate in a `finally`
(`SyncOrchestrator.cs:102`). If `Dispose` proceeds to `_flushGate.Dispose()` (line 262) while a
flush is still running, that `finally` throws `ObjectDisposedException` — on the fire-and-forget
`Task.Run` at line 234, whose only catch is `OperationCanceledException` (line 241). The result
is an unobserved task exception during shutdown.

Cancellation must therefore be the mechanism, with teardown either sequenced after the flush
observes it or made tolerant of the race.

## Acceptance Criteria

- [x] `SyncOrchestrator` implements `IDisposable` alongside `IAsyncDisposable`.
- [x] A lifetime `CancellationTokenSource` is linked into every flush, including manual
      `FlushAsync()` calls, and is cancelled by both disposal paths.
- [x] `Dispose()` signals cancellation and returns without waiting on the flush gate.
- [x] `DisposeAsync()` signals cancellation and awaits only the flush's acknowledgment of it,
      not its completion. No new configuration option is introduced.
- [x] No `ObjectDisposedException` escapes when disposal races an in-flight flush by either
      path, including from the `finally` that releases the gate.
- [x] Both paths are idempotent, and disposing by either route leaves `FlushAsync` throwing
      `ObjectDisposedException` as it does today.
- [x] Unit test: resolve `SyncOrchestrator` from a container, dispose the provider
      synchronously, no throw.
- [x] Unit test: dispose synchronously mid-flush; the flush stops, envelopes remain in the
      outbox, and no unobserved task exception is raised.
- [x] Unit test: dispose asynchronously mid-flush; disposal returns promptly rather than
      waiting out the retry budget.
- [x] Unit test: a manual `FlushAsync()` is cancelled by disposal.
- [x] `CabinetRegistrationTests.AddHyperwyc_NoConfiguration_RegistersCoreServices` reverted to
      synchronous `using`, and its explanatory comment removed.

## Resolution

Implemented as decided. Notes on what the implementation settled:

- **The gate and invoker are never disposed, by either path.** Disposing them is what would
  produce the `ObjectDisposedException` this issue set out to avoid, and neither holds a
  resource that requires release: the semaphore's `AvailableWaitHandle` is never touched, and
  the invoker was constructed with `disposeHandler: false`. Cancellation, not teardown, is the
  mechanism.
- **`DisposeAsync` waits by reacquiring the flush gate.** Once cancellation has been signalled
  that wait is inherently bounded — the loop breaks at the next envelope boundary — so it
  needed no timeout, and no new option.
- **A cancelled envelope is neither synced nor dead-lettered.** `SendWithRetryAsync` catches
  only `HttpRequestException`, so `OperationCanceledException` propagates and leaves the
  envelope untouched in the outbox. This was already true; a test now pins it, because
  dead-lettering work merely interrupted by shutdown would be a bad failure.
- **One test was rewritten before it shipped.** An initial version asserted `flush.IsCompleted`
  after `DisposeAsync`, which races: the gate is released in a `finally` fractionally before the
  task transitions to completed. It is replaced by a transport that takes a measurable moment to
  unwind, making "returned before" and "returned after" distinguishable without depending on
  scheduling.

### Follow-up noticed while here

`AddCoreServices` constructs `new HttpClientHandler()` for the orchestrator's transport and
nothing ever disposes it — the orchestrator explicitly does not own it. In practice it is an
application-lifetime singleton that dies with the process, so this is untidy rather than
harmful, but ownership of that handler is undefined and worth settling. Not filed as its own
item; it belongs with whatever revisits the orchestrator's transport, most likely issue #30,
which already has to decide how replays acquire credentials.

## Notes

- Filed during issue #31. Priority is P0 rather than a v1.0 item because it is a crash on a
  perfectly ordinary shutdown path, and the workaround (`await using`) is invisible to anyone
  who has not already hit it.
- Related: issue #34, which decided that shutdown is not a flush trigger on any platform. That
  is what reduces this issue to teardown hygiene and removes the need for a timeout option.
- Worth auditing whether any other registered type is `IAsyncDisposable`-only. At the time of
  filing, `SyncOrchestrator` is the only one, and no `ISyncStore` implementation is disposable
  at all — though issue #31 makes the container the owner of store construction, so a future
  disposable store would inherit exactly this problem.
