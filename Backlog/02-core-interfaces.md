# Issue 02 — Define Core Interfaces

## Summary

Define the public interface contracts that form Restyc's extensibility surface. These types live in the `Restyc` core package and are the seams against which every other component is written and tested.

## Interfaces to Define

### `ISyncStore`
CRUD operations for request/response envelopes.

```csharp
public interface ISyncStore
{
    Task<Envelope?> GetCachedResponseAsync(string url, CancellationToken ct = default);
    Task<IReadOnlyList<Envelope>> GetPendingOutboxAsync(CancellationToken ct = default);
    Task<IReadOnlyList<Envelope>> GetDueForRetryAsync(DateTimeOffset now, CancellationToken ct = default);
    Task UpsertAsync(Envelope envelope, CancellationToken ct = default);
    Task MarkSyncedAsync(string id, CancellationToken ct = default);
    Task MoveToDeadLetterAsync(string id, CancellationToken ct = default);
    Task InvalidateCacheForPrefixAsync(string urlPrefix, CancellationToken ct = default);
    Task ResetAsync(CancellationToken ct = default);
}
```

### `IConnectivityService`
Reports current network state and raises change events.

```csharp
public interface IConnectivityService
{
    bool IsConnected { get; }
    IObservable<bool> ConnectivityChanged { get; }
}
```

### `ISyncPolicy`
Declares cache-first vs API-first rules and whether write-triggered invalidation is enabled for a given request.

```csharp
public interface ISyncPolicy
{
    CacheStrategy GetStrategy(HttpRequestMessage request);
    bool ShouldInvalidateCacheOnWrite(HttpRequestMessage request);
    RetryOptions GetRetryOptions(HttpRequestMessage request);
}
```

### `IStalenessEvaluator`
Determines whether a cached response is still valid.

```csharp
public interface IStalenessEvaluator
{
    bool IsStale(Envelope cachedEnvelope, DateTimeOffset now);
}
```

### `IRestyc`
Top-level interface for the Restyc service, primarily exposing the event stream and store reset.

```csharp
public interface IRestyc
{
    IObservable<SyncEvent> SyncEvents { get; }
    Task ResetStoreAsync(CancellationToken ct = default);
}
```

## Supporting Types (value objects / enums)

- `CacheStrategy` enum: `CacheFirst`, `ApiFirst`, `CacheOnly`, `NetworkOnly`
- `RetryOptions` record: `MaxRetries`, `InitialDelay`, `BackoffMultiplier`
- `SyncEvent` record: `Type` (enum: `OnQueued`, `OnRetrying`, `OnSynced`, `OnFailed`, `OnUpdated`), `Url`, `Method`, `Timestamp`
- `SyncEventType` enum

## Acceptance Criteria

- [ ] All interfaces above are defined in `src/Restyc` with correct namespacing.
- [ ] Supporting types (`CacheStrategy`, `RetryOptions`, `SyncEvent`, `SyncEventType`) are defined.
- [ ] All types are documented with XML `<summary>` comments.
- [ ] No concrete implementations in this issue — interfaces and value types only.
- [ ] Unit tests are not required here, but public API should be reviewed for ergonomic .NET naming conventions.
