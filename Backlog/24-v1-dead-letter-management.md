# Issue 24 — [v1.0] Dead-Letter Queue Management

## Summary

Allow dead-lettered requests to be manually requeued or dismissed, and expose this capability through `IHyperwyc` and (optionally) a UI in the MAUI sample app.

## Background

An envelope the server refuses — a `4xx` other than 408 or 429 — moves to dead-letter and `OnFailed` is published. (Until the [ADR 0004](../docs/decisions/0004-default-to-removal.md) audit this also happened when a retry budget ran out; there is no budget now, so a server refusal is the only route in.) Without a management API, these envelopes are stuck permanently until `ResetStoreAsync()` wipes everything. This issue adds surgical control: requeue individual items — which now means "the reason it was refused has been dealt with, try again" rather than "give it more attempts" — or dismiss them permanently.

## `IHyperwyc` Extensions

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

- [ ] `RequeueDeadLetteredAsync` and `DismissDeadLetteredAsync` added to `IHyperwyc`.
- [ ] `IHyperwycStore` extended with `RequeuAsync(string id)` and `DeleteAsync(string id)` (or equivalent).
- [ ] Both operations implemented in `InMemoryStore` and `CabinetStore`.
- [ ] Requeue on an online device triggers an immediate flush.
- [ ] `DiagnosticsPage` in the MAUI sample updated with per-item and bulk dismiss actions.
- [ ] Unit tests cover: requeue resets retry fields, requeue triggers flush, dismiss removes envelope, requeue on unknown id is a no-op.

## Notes

- "Dismiss All" can be implemented as a loop over `GetDeadLetteredAsync()` + `DismissDeadLetteredAsync()`.
- A "Requeue All" convenience method may be added as a follow-up.
