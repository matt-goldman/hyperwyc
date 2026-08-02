# Issue 07 — `HyperwycHandler` — Offline Queue Path

## Summary

Implement the offline branch inside `HyperwycHandler`: when the device has no connectivity, serialise the request into the local store, publish `OnQueued`, and return a synthetic "queued" response to the caller.

## Background

When `IConnectivityService.IsConnected` is `false`, Hyperwyc must not drop the request. Instead, the envelope is persisted to the outbox so the sync flush (issue #11) can replay it when connectivity returns.

## Behaviour (Offline Path)

1. `HyperwycHandler.SendAsync` checks `IConnectivityService.IsConnected`.
2. If **offline**:
   - Create an `Envelope` via `Envelope.ForRequest(request)`.
   - Persist it to `ISyncStore` with `IsSynced = false`.
   - Publish `OnQueued` via `SyncEventStream`.
   - Return a synthetic `HttpResponseMessage` to the caller (e.g. `503 Service Unavailable` with a `X-Hyperwyc-Status: Queued` header), so the calling code does not throw an unhandled exception.
3. For **read requests** (GET/HEAD/OPTIONS) while offline:
   - Check the cache first; if a response exists (even if stale), return it.
   - If no cache entry exists, return the synthetic queued/unavailable response.
   - Read requests are **not** added to the outbox.

## Acceptance Criteria

- [x] Offline write path persists the envelope and publishes `OnQueued`.
- [x] Offline read path serves from cache when available.
- [x] Synthetic response returned for offline writes (caller does not throw on the handler level).
- [x] Offline read with no cache returns a distinct synthetic `HttpResponseMessage` (e.g. `503` with `X-Hyperwyc-Status: Offline`).
- [x] Unit tests use `InMemorySyncStore` and a mock `IConnectivityService` (online = false).
- [x] Tests cover: offline write added to outbox, offline GET with cached response returns cache, offline GET without cache returns 503, `OnQueued` event published.

## Notes

- The exact HTTP status code and headers for synthetic responses should be decided consistently (consider a `HyperwycResponseFactory` internal helper).
- Callers are expected to inspect the `X-Hyperwyc-Status` header if they want to know whether a response came from the queue or the network.
