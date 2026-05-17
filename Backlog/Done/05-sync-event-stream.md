# Issue 05 — `SyncEventStream` — Hand-Rolled `IObservable<SyncEvent>`

## Summary

Implement `SyncEventStream`, hyperwyc's internal reactive event publisher, without taking a dependency on `System.Reactive`.

## Background

hyperwyc exposes sync lifecycle events as `IObservable<SyncEvent>`. The `IObservable<T>` interface is part of the BCL (`System`), so no third-party package is required for production. A minimal hand-rolled subject is sufficient; consumers who want Rx operators (`.Where()`, `.Throttle()`, etc.) add `System.Reactive` themselves.

Plain .NET events are not exposed — `IObservable<T>` is strictly more capable and consumers can achieve event-style consumption with a one-line `.Subscribe(...)` call.

## Event Types

| `SyncEventType` | Trigger |
|---|---|
| `OnQueued` | Request persisted to outbox (offline) |
| `OnRetrying` | Retry attempt initiated |
| `OnSynced` | Outbound request successfully delivered |
| `OnFailed` | Request moved to dead-letter after max retries |
| `OnUpdated` | Cached response refreshed from API |

## Acceptance Criteria

- [x] `SyncEventStream` class implemented in `src/hyperwyc` with no dependency on `System.Reactive`.
- [x] Implements a minimal subject: observers can subscribe, receive events, and unsubscribe.
- [x] Thread-safe: multiple subscribers receiving events concurrently do not corrupt state.
- [x] Subscriptions return an `IDisposable` that removes the subscriber on `Dispose()`.
- [x] `SyncEventStream.Publish(SyncEvent)` is internal; only `hyperwycHandler` and the sync orchestrator call it.
- [x] `Ihyperwyc.SyncEvents` exposes the `IObservable<SyncEvent>` publicly (read-only projection).
- [x] A `OnCompleted` is called on all subscribers when the stream is disposed.
- [x] Unit tests cover: subscribe/receive, unsubscribe, multi-subscriber fan-out, dispose behaviour.

## Notes

- Exceptions inside a subscriber's `OnNext` must not propagate to other subscribers or crash the pipeline.
- Errors in the stream itself call `OnError` on all current subscribers.
