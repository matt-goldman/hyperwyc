# Issue 04 — `InMemorySyncStore` Implementation

## Summary

Implement `InMemorySyncStore`, the in-process, non-persistent `ISyncStore` bundled with the core `Restyc` package. It is the default store used in testing and in contexts where persistence is not needed.

## Background

The `Restyc` core package ships without any file-based or database dependency. `InMemorySyncStore` satisfies `ISyncStore` using a thread-safe in-memory dictionary, allowing all other components (especially `RestycHandler`) to be tested without a real store.

## Acceptance Criteria

- [x] `InMemorySyncStore : ISyncStore` implemented in `src/Restyc`.
- [x] Thread-safe; uses `SemaphoreSlim` or `ConcurrentDictionary` appropriately.
- [x] Implements all `ISyncStore` methods:
  - `GetCachedResponseAsync` — returns the envelope for a URL only if it has a `Response` and is not dead-lettered.
  - `GetPendingOutboxAsync` — returns envelopes with `IsSynced = false` and `IsDeadLettered = false`, ordered by `CreatedUtc`.
  - `GetDueForRetryAsync` — returns pending envelopes whose `NextRetryUtc <= now`.
  - `UpsertAsync` — inserts or replaces by `Id`.
  - `MarkSyncedAsync` — sets `IsSynced = true` on the matching envelope.
  - `MoveToDeadLetterAsync` — sets `IsDeadLettered = true`.
  - `InvalidateCacheForPrefixAsync` — removes `Response` from any envelope whose `Url` starts with the given prefix.
  - `ResetAsync` — clears all envelopes.
- [x] Unit tests cover all methods including edge cases (empty store, missing ID, prefix matching).

## Notes

- This store does not survive process restarts; that is expected and documented.
- `Restyc.Cabinet` provides durable persistence — see issue #14.
