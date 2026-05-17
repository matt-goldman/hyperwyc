# Issue 06 — `hyperwycHandler` — Online Request Path

## Summary

Implement the online path inside `hyperwycHandler`: when the device is connected, route the request normally, cache the response (for reads), and invalidate stale cache entries (following writes).

## Background

`hyperwycHandler : DelegatingHandler` is the central pipeline component. This issue covers only the **online** branch; the offline/queue branch is issue #07.

## Behaviour (Online Path)

### Write requests (POST, PUT, PATCH, DELETE)
1. Send the request immediately via `base.SendAsync(...)`.
2. On a 2xx response:
   - Invalidate cached GET responses for the same URL prefix (default on; configurable via `ISyncPolicy.ShouldInvalidateCacheOnWrite`).
   - Optionally cache the response itself according to the active `ISyncPolicy`.
   - Mark any existing envelope for this URL as synced.
   - Publish `OnSynced`.
3. On a non-2xx or exception: bubble up to caller — retry handling is the orchestrator's responsibility (issue #11).

### Read requests (GET, HEAD, OPTIONS)
1. Consult the staleness evaluator:
   - If a fresh cached response exists: return it immediately (do not call `base.SendAsync`).
   - If stale or missing: call `base.SendAsync`, store the result in the cache, publish `OnUpdated`, return response to caller.
2. See issue #09 for the full cache read/write logic.

## Acceptance Criteria

- [x] `hyperwycHandler` class scaffolded in `src/hyperwyc`; constructor accepts `ISyncStore`, `IConnectivityService`, `ISyncPolicy`, `IStalenessEvaluator`, `SyncEventStream`, and `hyperwycOptions`.
- [x] Online write path implemented as described.
- [x] Online read path delegates to response cache (stubs acceptable; full logic in issue #09).
- [x] Handler correctly calls `base.SendAsync` and returns the `HttpResponseMessage` to the caller.
- [x] Unit tests use `InMemorySyncStore` and a mock `IConnectivityService` (online = true).
- [x] Tests cover: 2xx write triggers invalidation, non-2xx write does not invalidate, GET with fresh cache skips network call.

## Notes

- `hyperwycHandler` depends on `IConnectivityService`; it does not subscribe to the connectivity stream directly — that is the sync flush orchestrator's job (issue #11).
- Handler placement in the pipeline (before/after auth handlers) is documented in the README and does not affect implementation here.
