# Issue 23 — [v1.0] In-App Diagnostics View — the Outbox

## Summary

Provide a read-only diagnostics surface so developers (and end users) can inspect the current state of the Hyperwyc store: what is pending in the outbox, and why it is not moving.

> **Narrowed by [66](66-dead-letter-store-fails-the-scope-test.md), which has landed.** There is no dead-letter store any more, so half of what this item proposed reading has nothing behind it. **The other half survives intact and matters more than it did**: the outbox is named explicitly by scope-test Q4, and since 66 a transport failure — which publishes no event — is the *only* outcome Hyperwyc still writes down. This is the read path to it. Everything below that says "dead-lettered" should be read as struck out; everything about pending items stands.

## Background

Without visibility into the store, it is hard to debug sync problems. A diagnostics view exposes the data already in the store in a consumable form, without requiring direct access to the underlying `IHyperwycStore`.

## `IHyperwyc` Extensions

Add the following to `IHyperwyc` (or a new `IHyperwycDiagnostics` interface):

```csharp
Task<IReadOnlyList<PendingItem>> GetPendingOutboxAsync(CancellationToken ct = default);
Task<IReadOnlyList<DeadLetteredItem>> GetDeadLetteredAsync(CancellationToken ct = default);
```

Where:

```csharp
public record PendingItem(
    string Id, string CorrelationId, string Method, string Url,
    DateTimeOffset CreatedUtc, int RetryCount, DeliveryOutcome? LastOutcome);

public record DeadLetteredItem(
    string Id, string CorrelationId, string Method, string Url,
    DateTimeOffset CreatedUtc, int RetryCount, DeliveryOutcome? LastOutcome);
```

## MAUI Sample View

Add a `DiagnosticsPage` to `Hyperwyc.Sample/MauiApp` that:
- Lists all pending items with method, URL, created time, and retry count.
- Lists all dead-lettered items with the same fields.
- Has a "Refresh" button.

This page is for developer reference and POC validation; it does not need to be a polished production component.

## Acceptance Criteria

- [ ] `GetPendingOutboxAsync()` and `GetDeadLetteredAsync()` added to `IHyperwyc` (or a diagnostics interface).
- [ ] Implemented by the concrete Hyperwyc service, delegating to `IHyperwycStore`.
- [ ] `PendingItem` and `DeadLetteredItem` record types defined, carrying `CorrelationId` and
      `LastOutcome` from [issue 40](Done/40-surface-deferred-outcomes.md).
- [ ] A read path exists for dead-lettered envelopes, which is what turns 40's persisted failure
      detail into something observable after a restart.
- [ ] `DiagnosticsPage` added to the MAUI sample app.
- [ ] Unit tests cover both query methods (empty, populated, mixed states).

## Notes

- **[Issue 40](Done/40-surface-deferred-outcomes.md) did its half.** The failure detail is now
  persisted on `Envelope.LastOutcome` — status, reason, response body, transport error, attempt
  count, and whether Hyperwyc has given up. Nothing can enumerate it yet, which is this issue.
  Surface it as-is rather than reshaping it: it is the same record the events hand out, and one
  shape is the point.
- Also worth showing on pending items, not just dead-lettered ones. A `TransportFailure` recorded
  against a pending envelope is precisely what explains an outbox that will not drain, and it
  publishes no event, so this view is the only place it can be seen.

- These methods are read-only; they do not modify the store.
- Dead-letter requeue/dismiss is tracked separately in issue #24.
