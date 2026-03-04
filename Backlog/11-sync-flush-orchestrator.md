# Issue 11 — Outbound Sync Flush Orchestrator

## Summary

Implement the component that listens for connectivity restoration, drains the outbox in order, and manages the single-flush semaphore and connectivity event debounce.

## Background

When a device regains connectivity, all envelopes queued in the outbox must be replayed in the order they were created. Rapid connectivity toggling (e.g. elevator, tunnel) must not trigger redundant concurrent flushes.

## Behaviour

### Connectivity event debounce
- Subscribe to `IConnectivityService.ConnectivityChanged`.
- Debounce `true` (connected) events by 2 seconds (configurable via `RestycOptions.ConnectivityDebounceDelay`).
- On debounced "connected" event, trigger a flush.

### Flush loop
1. Acquire `SemaphoreSlim(1,1)`. If the semaphore is already held, skip — another flush is in progress.
2. Call `ISyncStore.GetPendingOutboxAsync()` to get all unsynced envelopes, ordered by `CreatedUtc`.
3. For each envelope:
   - Build an `HttpRequestMessage` from the stored data.
   - Re-inject the `Idempotency-Key` header (same value as the original send).
   - Call `base.SendAsync` (or a dedicated inner handler reference).
   - On 2xx: call `ISyncStore.MarkSyncedAsync`, publish `OnSynced`, invalidate cache prefix (if configured).
   - On failure: hand off to the retry policy (issue #12). Publish `OnRetrying` before each retry.
4. Release the semaphore on completion or exception.

### Failure semantics
- A non-2xx response or timeout is a **failure**; it is handled by the retry budget (issue #12).
- A connectivity drop during flush is not a failure in this component — the retry policy handles it; a fresh flush will be triggered when connectivity returns again.

## Acceptance Criteria

- [ ] `SyncOrchestrator` (or similar name) implemented in `src/Restyc`.
- [ ] Subscribes to `IConnectivityService.ConnectivityChanged`; debounce configurable.
- [ ] Semaphore prevents concurrent flushes.
- [ ] Envelopes processed in `CreatedUtc` ascending order.
- [ ] `OnSynced` published on each successful delivery.
- [ ] `OnRetrying` published before each retry attempt.
- [ ] Implements `IDisposable` / `IAsyncDisposable` to release the connectivity subscription.
- [ ] Unit tests cover: flush on connect, no double flush, ordering, `OnSynced` event, semaphore guard.

## Notes

- The orchestrator should also be triggerable manually (e.g. from `IRestyc` for a UI-initiated sync button).
- App-start flush (trigger flush on startup if online) can be a configuration flag in `RestycOptions`.
