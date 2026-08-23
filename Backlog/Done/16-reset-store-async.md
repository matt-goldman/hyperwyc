# Issue 16 — `IHyperwyc.ResetStoreAsync()` — Logout / Cache Clearing

## Summary

Implement `IHyperwyc.ResetStoreAsync()`, which clears all Hyperwyc-managed state from the store — typically called on user logout or when a full cache wipe is needed.

## Background

When a user logs out, locally cached data and any queued requests should be discarded. `ResetStoreAsync()` is the single call that clears everything: the outbox, the cache, and the dead-letter collection.

## Behaviour

1. Acquire the sync flush semaphore (or wait for any in-flight flush to complete) before wiping.
2. Call `ISyncStore.ResetAsync()`.
3. After the store is cleared, release the semaphore.
4. This method does **not** stop the orchestrator or unsubscribe from connectivity events — Hyperwyc continues operating normally after the reset, just with an empty store.

## Acceptance Criteria

- [x] `ResetStoreAsync(CancellationToken ct = default)` implemented on the concrete `Hyperwyc` service class that backs `IHyperwyc`.
- [x] In-flight flush is awaited or the semaphore is acquired before wiping.
- [x] `ISyncStore.ResetAsync()` is called.
- [x] Orchestrator continues to function after reset (no broken state).
- [x] Unit tests cover: reset on empty store, reset with pending outbox entries, reset while flush
      in progress, and that a flush writing back after the wipe does not resurrect an envelope.
- [x] Reset does **not** flush first — see below.

## Notes

- The MAUI sample app (issue #19) should wire `ResetStoreAsync` to a "logout" button to validate the end-to-end behaviour.
- For identity-scoped stores (v2.0 user-scoped store), this method resets only the current user's partition. That scoping is out of scope for v0.1.

## Where it ended up

`ResetStoreAsync` moved from `HyperwycService` onto `SyncOrchestrator`, because it needs two
things only the orchestrator has: the flush gate and the scheduled follow-up. `HyperwycService`
now delegates, and no longer takes an `ISyncStore` at all.

### The gate has to be acquired, not merely poked

The first cut delegated to `FlushAsync` before wiping, on the reading that this would let an
in-flight flush finish. It does not. `FlushAsync` acquires the gate with `WaitAsync(0)` — a
try-acquire that **returns immediately when a flush is already running** — so the reset went
straight on to wipe the store underneath a flush that was still iterating.

The consequence is worse than a wasted call. A flush holds a list of envelopes read before the
wipe and keeps acting on them, and both `DeferAsync` and `MarkSyncedAsync` write back. A
transiently-failing envelope is therefore `UpsertAsync`-ed *after* the store has been emptied and
is **resurrected**. On the logout this method exists for, that means the previous user's queued
write reappears in the store — and is then replayed under the next user's credentials.

`SyncOrchestrator.ResetStoreAsync` now takes the gate blocking, wipes, and cancels the scheduled
follow-up (after the wipe, so a pass scheduled by the flush it just waited out is also cleared).
Both behaviours are pinned by tests that fail against the previous implementation.

### Reset discards; it does not deliver

The first cut also flushed before wiping, to avoid throwing away queued work. That is the wrong
default, and on the case it was written for it is actively harmful.

Replays traverse the application's pipeline
([ADR 0002](../../docs/decisions/0002-replays-traverse-the-pipeline.md)), so a flush during
logout runs each queued write through the app's auth handler. The sample removed the token first,
so every replay would have gone out unauthenticated, returned `401`, been classified permanent by
`IsPermanentFailure`, dead-lettered — and then wiped anyway. A burst of doomed requests and a pile
of `OnFailed` events, on the logout path, to achieve exactly what wiping alone achieves.

Sequencing it the other way does not make it Hyperwyc's decision either. Whether pending writes
are worth delivering before a reset, and whether the credentials to deliver them still exist, is
knowledge the application has and Hyperwyc does not. Two operations that compose:

```csharp
await hyperwyc.FlushAsync();        // optional, and only while the token is still valid
await hyperwyc.ResetStoreAsync();
```

The sample now resets *then* clears the token, and does not flush.

## A limitation worth knowing about

`CabinetSyncStore.ResetAsync` enumerates via `GetAllAsync` and removes records one by one, so it
**decrypts before it deletes**. A store that cannot be read therefore cannot be reset — which
answers the open question in [issue 49](../49-unreadable-store-recovery.md), and answers it badly:
`ResetStoreAsync` is the remedy 49 recommends for an unreadable store, and it does not work in
exactly that case. Deleting the files rather than the records would fix it. Recorded against 49
rather than fixed here, since it belongs with that item's decision.
