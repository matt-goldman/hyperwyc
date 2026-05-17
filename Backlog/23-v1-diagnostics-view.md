# Issue 23 — [v1.0] In-App Diagnostics View — Unsynced and Dead-Lettered Records

## Summary

Provide a read-only diagnostics surface so developers (and end users) can inspect the current state of the hyperwyc store: what's pending in the outbox and what's stuck in dead-letter.

## Background

Without visibility into the store, it is hard to debug sync problems. A diagnostics view exposes the data already in the store in a consumable form, without requiring direct access to the underlying `ISyncStore`.

## `Ihyperwyc` Extensions

Add the following to `Ihyperwyc` (or a new `IhyperwycDiagnostics` interface):

```csharp
Task<IReadOnlyList<PendingItem>> GetPendingOutboxAsync(CancellationToken ct = default);
Task<IReadOnlyList<DeadLetteredItem>> GetDeadLetteredAsync(CancellationToken ct = default);
```

Where:

```csharp
public record PendingItem(string Id, string Method, string Url, DateTimeOffset CreatedUtc, int RetryCount);
public record DeadLetteredItem(string Id, string Method, string Url, DateTimeOffset CreatedUtc, int RetryCount);
```

## MAUI Sample View

Add a `DiagnosticsPage` to `hyperwyc.Sample/MauiApp` that:
- Lists all pending items with method, URL, created time, and retry count.
- Lists all dead-lettered items with the same fields.
- Has a "Refresh" button.

This page is for developer reference and POC validation; it does not need to be a polished production component.

## Acceptance Criteria

- [ ] `GetPendingOutboxAsync()` and `GetDeadLetteredAsync()` added to `Ihyperwyc` (or a diagnostics interface).
- [ ] Implemented by the concrete hyperwyc service, delegating to `ISyncStore`.
- [ ] `PendingItem` and `DeadLetteredItem` record types defined.
- [ ] `DiagnosticsPage` added to the MAUI sample app.
- [ ] Unit tests cover both query methods (empty, populated, mixed states).

## Notes

- These methods are read-only; they do not modify the store.
- Dead-letter requeue/dismiss is tracked separately in issue #24.
