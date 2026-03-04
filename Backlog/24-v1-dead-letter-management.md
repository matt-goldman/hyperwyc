# Issue 24 — [v1.0] Dead-Letter Queue Management

## Summary

Allow dead-lettered requests to be manually requeued or dismissed, and expose this capability through `IRestyc` and (optionally) a UI in the MAUI sample app.

## Background

After the retry budget is exhausted (issue #12), an envelope moves to dead-letter and `OnFailed` is published. Without a management API, these envelopes are stuck permanently until `ResetStoreAsync()` wipes everything. This issue adds surgical control: requeue individual items for another retry attempt, or dismiss them permanently.

## `IRestyc` Extensions

```csharp
// Requeue: move back to the outbox with RetryCount = 0, IsSynced = false, IsDeadLettered = false
Task RequeueDeadLetteredAsync(string id, CancellationToken ct = default);

// Dismiss: permanently delete the envelope from the store
Task DismissDeadLetteredAsync(string id, CancellationToken ct = default);
```

## Behaviour

### Requeue
1. Find the envelope by `id`.
2. Reset `RetryCount = 0`, `NextRetryUtc = null`, `IsDeadLettered = false`, `IsSynced = false`.
3. Upsert the envelope.
4. Trigger a sync flush if the device is currently online.

### Dismiss
1. Delete the envelope from the store entirely.
2. No events published (already `OnFailed`).

## MAUI Sample View

Extend `DiagnosticsPage` (issue #23) with:
- A "Requeue" button per dead-lettered item.
- A "Dismiss" button per dead-lettered item.
- A "Dismiss All" button.

## Acceptance Criteria

- [ ] `RequeueDeadLetteredAsync` and `DismissDeadLetteredAsync` added to `IRestyc`.
- [ ] `ISyncStore` extended with `RequeuAsync(string id)` and `DeleteAsync(string id)` (or equivalent).
- [ ] Both operations implemented in `InMemorySyncStore` and `CabinetSyncStore`.
- [ ] Requeue on an online device triggers an immediate flush.
- [ ] `DiagnosticsPage` in the MAUI sample updated with per-item and bulk dismiss actions.
- [ ] Unit tests cover: requeue resets retry fields, requeue triggers flush, dismiss removes envelope, requeue on unknown id is a no-op.

## Notes

- "Dismiss All" can be implemented as a loop over `GetDeadLetteredAsync()` + `DismissDeadLetteredAsync()`.
- A "Requeue All" convenience method may be added as a follow-up.
