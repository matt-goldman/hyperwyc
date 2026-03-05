# Issue 14 — `Restyc.Cabinet` — `CabinetSyncStore` Provider Package

## Summary

Implement `CabinetSyncStore`, the default durable `ISyncStore` backed by Cabinet, shipping in the separate `Restyc.Cabinet` NuGet package.

## Background

[Cabinet](https://github.com/matt-goldman/cabinet) is a .NET document store with pluggable index providers. It is a natural fit for Restyc's envelope model: each envelope is a self-contained document, and Cabinet's custom index support maps directly onto the query patterns Restyc needs.

## Cabinet Indexing Strategy

```csharp
new EnvelopeIndex()
    .WithKey(e => e.Url)           // GET cache lookups
    .WithKey(e => e.IsSynced)      // outbox queries
    .WithKey(e => e.NextRetryUtc)  // retry scheduler
    .WithKey(e => e.Method);       // filter by HTTP verb
```

This enables efficient answers to:
- Get the cached response for `GET /api/notes`
- Get all unsynced outbound envelopes (outbox)
- Get all envelopes due for retry
- Filter by HTTP method

## Acceptance Criteria

- [ ] `Restyc.Cabinet` project created (see issue #01); references `Restyc` and the Cabinet NuGet package.
- [ ] `CabinetSyncStore : ISyncStore` implements all `ISyncStore` methods using Cabinet.
- [ ] Cabinet indexes configured as shown above.
- [ ] Database file path configurable via constructor: `new CabinetSyncStore("restyc.db")`.
- [ ] `ResetAsync` deletes or truncates all Restyc-managed collections.
- [ ] `InvalidateCacheForPrefixAsync` clears `Response` on all envelopes whose `Url` starts with the given prefix.
- [ ] `GetPendingOutboxAsync` returns envelopes ordered by `CreatedUtc` ascending.
- [ ] Integration tests (using a real Cabinet in-process instance) cover all store methods.

## Notes

- Cabinet's mobile-safety (pure managed code, no native libs) is a deliberate choice for MAUI support.
- If Cabinet's API evolves, this issue should be updated to track the correct package version.
- `InMemorySyncStore` (issue #04) remains the default for unit tests; `CabinetSyncStore` is for production use.
