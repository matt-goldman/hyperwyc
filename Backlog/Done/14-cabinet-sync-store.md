# Issue 14 — `hyperwyc.Cabinet` — `CabinetSyncStore` Provider Package

## Summary

Implement `CabinetSyncStore`, the default durable `ISyncStore` backed by Cabinet, shipping in the separate `hyperwyc.Cabinet` NuGet package.

## Background

[Cabinet](https://github.com/matt-goldman/cabinet) is a .NET document store with pluggable index providers. It is a natural fit for hyperwyc's envelope model: each envelope is a self-contained document, and Cabinet's custom index support maps directly onto the query patterns hyperwyc needs.

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

- [x] `hyperwyc.Cabinet` project created (see issue #01); references `hyperwyc` and the Cabinet NuGet package.
- [x] `CabinetSyncStore : ISyncStore` implements all `ISyncStore` methods using Cabinet.
- [x] Cabinet indexes configured as shown above.
- [x] Database file path configurable via constructor: `new CabinetSyncStore("hyperwyc.db")`.
- [x] `ResetAsync` deletes or truncates all hyperwyc-managed collections.
- [x] `InvalidateCacheForPrefixAsync` clears `Response` on all envelopes whose `Url` starts with the given prefix.
- [x] `GetPendingOutboxAsync` returns envelopes ordered by `CreatedUtc` ascending.
- [x] Integration tests (using a real Cabinet in-process instance) cover all store methods.

## Notes

- Cabinet's mobile-safety (pure managed code, no native libs) is a deliberate choice for MAUI support.
- If Cabinet's API evolves, this issue should be updated to track the correct package version.
- `InMemorySyncStore` (issue #04) remains the default for unit tests; `CabinetSyncStore` is for production use.
- The `EnvelopeIndex().WithKey()` indexing strategy shown in the "Cabinet Indexing Strategy" section above does not exist in Cabinet 1.0.7. The actual implementation uses `RecordSet<Envelope>` with `GetAllAsync()` + in-memory LINQ queries (enabled by Cabinet's built-in in-memory cache). The indexing section is aspirational and should be revisited if Cabinet adds key-based index support in a future version.
- `CabinetSyncStore` provides two constructors: a single-arg convenience constructor that derives an encryption key via SHA-256 of the path (for development/testing only) and a two-arg constructor accepting an explicit 32-byte key (for production use).
