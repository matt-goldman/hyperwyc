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

## Behaviour

`SyncOrchestrator` should implement both `IDisposable` and `IAsyncDisposable`.

The synchronous path is not a straight port of the asynchronous one. `DisposeAsync` currently
awaits the flush semaphore so an in-flight flush can finish:

```csharp
await _flushGate.WaitAsync().ConfigureAwait(false);
_flushGate.Release();
```

A synchronous `Dispose` cannot await that without blocking, and blocking on a flush during
application shutdown risks a deadlock on single-threaded synchronisation contexts. The
decision to make is what `Dispose()` guarantees:

- **Abandon the in-flight flush.** Cancel the debounce, unsubscribe from connectivity, dispose
  the semaphore and invoker, and do not wait. Fast and deadlock-free; an in-flight replay is
  cut short, though envelopes stay in the outbox and are retried on next start.
- **Wait with a bounded timeout.** `_flushGate.Wait(timeout)` before tearing down. Preserves
  most of the current guarantee at the cost of a possible short shutdown delay.

The first is the more conventional choice for a `Dispose` that has an async counterpart, and
the outbox makes an abandoned flush recoverable — nothing is lost, it is retried.

## Acceptance Criteria

- [ ] `SyncOrchestrator` implements `IDisposable` alongside `IAsyncDisposable`.
- [ ] Decision recorded on what the synchronous path guarantees for an in-flight flush.
- [ ] Both paths are idempotent, and disposing by either route leaves `FlushAsync` throwing
      `ObjectDisposedException` as it does today.
- [ ] Unit test: resolve `SyncOrchestrator`, dispose the provider synchronously, no throw.
- [ ] Unit test: dispose asynchronously, existing behaviour unchanged.
- [ ] `CabinetRegistrationTests.AddHyperwyc_NoConfiguration_RegistersCoreServices` reverted to
      synchronous `using`, and its explanatory comment removed.

## Notes

- Filed during issue #31. Priority is P0 rather than a v1.0 item because it is a crash on a
  perfectly ordinary shutdown path, and the workaround (`await using`) is invisible to anyone
  who has not already hit it.
- Worth auditing whether any other registered type is `IAsyncDisposable`-only. At the time of
  filing, `SyncOrchestrator` is the only one, and no `ISyncStore` implementation is disposable
  at all — though issue #31 makes the container the owner of store construction, so a future
  disposable store would inherit exactly this problem.
