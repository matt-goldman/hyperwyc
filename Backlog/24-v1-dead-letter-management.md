# Issue 24 — [v1.0] Dead-Letter Queue Management

## Status

⛔ **Closed unbuilt, 2026-09-09 — dissolved rather than superseded.** It was never implemented, so there is nothing for a later decision to supersede. Its two halves have moved to different items, which is the tell that they were only ever bundled together by a model that turned out to be wrong.

**This does not depend on [66](66-dead-letter-store-fails-the-scope-test.md) resolving.** What condemns this item is the vocabulary finding, which is settled independently: dead-lettering means *could not be delivered*, and it was being applied to requests that had been. There is no branch of 66 under which "dead-letter management" is the right name for a real capability:

| If 66 resolves to | What "requeue and dismiss" becomes |
|---|---|
| Retain nothing after delivery | Nothing to manage. Requeue is the application making a *new* write, which it can already do at the call site |
| Rename to *delivered*, retain everything | Dismiss is a retention operation, so it belongs to [67](67-configurable-response-retention.md). Requeue still is not un-failing anything |
| Rejected, keep the store as it is | The same. The framing was condemned before 66 was filed |

### Where the two halves went

- **Requeue** → [69](69-expiring-queued-writes.md). It is only coherent for a request that was *never delivered*, and the only future in which such a thing is sitting in a store waiting to be acted on is if expiry retains rather than discards. That would be the first artefact in this codebase to deserve the name dead-letter.
- **Dismiss** → [67](67-configurable-response-retention.md), and the eviction thinking in [42](42-cache-eviction.md). Removing something the store is holding on purpose is a retention decision, and has nothing to do with delivery.

### It was independently stale anyway

Worth recording, because it shows how far the model had drifted from the code before anyone noticed:

- *"An envelope the server refuses — a `4xx` other than 408 or 429"* — untrue since any non-`2xx` became final. The body even acknowledges the [ADR 0004](../docs/decisions/0004-default-to-removal.md) audit in a parenthetical and then keeps the classification it removed.
- The requeue steps reset `RetryCount` and `NextRetryUtc`. Neither field has existed since that audit.

Both errors are the same shape as the ones the documentation review kept finding: a decision removed the mechanism and nothing went back for the prose describing it.

---

*Original item preserved below. It records what was planned and why, which is the reason a closed item stays where it is.*

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
