# Issue 16 — `Ihyperwyc.ResetStoreAsync()` — Logout / Cache Clearing

## Summary

Implement `Ihyperwyc.ResetStoreAsync()`, which clears all hyperwyc-managed state from the store — typically called on user logout or when a full cache wipe is needed.

## Background

When a user logs out, locally cached data and any queued requests should be discarded. `ResetStoreAsync()` is the single call that clears everything: the outbox, the cache, and the dead-letter collection.

## Behaviour

1. Acquire the sync flush semaphore (or wait for any in-flight flush to complete) before wiping.
2. Call `ISyncStore.ResetAsync()`.
3. After the store is cleared, release the semaphore.
4. This method does **not** stop the orchestrator or unsubscribe from connectivity events — hyperwyc continues operating normally after the reset, just with an empty store.

## Acceptance Criteria

- [ ] `ResetStoreAsync(CancellationToken ct = default)` implemented on the concrete `hyperwyc` service class that backs `Ihyperwyc`.
- [ ] In-flight flush is awaited or the semaphore is acquired before wiping.
- [ ] `ISyncStore.ResetAsync()` is called.
- [ ] Orchestrator continues to function after reset (no broken state).
- [ ] Unit tests cover: reset on empty store, reset with pending outbox entries, reset while flush in progress.

## Notes

- The MAUI sample app (issue #19) should wire `ResetStoreAsync` to a "logout" button to validate the end-to-end behaviour.
- For identity-scoped stores (v2.0 user-scoped store), this method resets only the current user's partition. That scoping is out of scope for v0.1.
