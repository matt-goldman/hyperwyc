# Issue 28 — Retry State Is Never Persisted

## Summary

`Envelope.RetryCount`, `Envelope.NextRetryUtc` and `ISyncStore.GetDueForRetryAsync` are
implemented, indexed and unit-tested in both stores, but `SyncOrchestrator` never writes
or reads them. Retry budget lives only in Polly's in-process pipeline, so a process
restart mid-flush silently resets it.

## Background

`SyncOrchestrator.SendWithRetryAsync` builds a Polly `ResiliencePipeline` per envelope,
executes the send inside it, and on final failure calls `MoveToDeadLetterAsync`. The
envelope's own retry fields are untouched throughout. `GetDueForRetryAsync` has no call
site outside tests.

Consequences:

- **Restart loses the budget.** An app killed during a flush (routine on mobile) restarts
  with `RetryCount = 0` and retries a request that had already exhausted most of its
  attempts.
- **Long backoffs don't survive backgrounding.** With the default 5 retries at 2s
  exponential, a flush can stay in-process for well over a minute — longer than a
  backgrounded mobile app is guaranteed to live.
- **Diagnostics will report zero.** Issue #23 surfaces `RetryCount` per item; it will
  always read 0.

## Behaviour

Decide between two models, then make the code and the docs agree:

### Option A — Persist between attempts (preferred)
Write `RetryCount` and `NextRetryUtc` to the store on each failed attempt. A flush drains
`GetPendingOutboxAsync` for never-attempted envelopes plus `GetDueForRetryAsync(now)` for
scheduled ones. Polly still computes the backoff delay; the orchestrator persists the
resulting next-attempt time rather than sleeping through it in-process. Dead-letter when
`RetryCount` exceeds `RetryOptions.MaxRetries`.

### Option B — Keep retries in-process
Accept that the budget is per-flush, and delete `NextRetryUtc`, `RetryCount` and
`GetDueForRetryAsync` from `ISyncStore` and `Envelope` as dead surface. Requires
documenting that an interrupted flush restarts the budget.

## Acceptance Criteria

- [ ] Decision recorded between Option A and Option B.
- [ ] If A: `RetryCount` incremented and `NextRetryUtc` set on every failed attempt, persisted via `UpsertAsync`.
- [ ] If A: flush drains both never-attempted and retry-due envelopes.
- [ ] If A: dead-lettering is driven by the persisted `RetryCount`, not by pipeline exhaustion alone.
- [ ] If A: unit tests cover budget survival across orchestrator instances (simulating a restart).
- [ ] If B: unused members removed from `ISyncStore`, `Envelope`, both store implementations and their tests.
- [ ] TECHNICAL_PLAN's Outbound Sync section describes whichever model ships.

## Notes

- Discovered while reconciling the docs against the code; not previously tracked.
- Option A also gives issue #24 (dead-letter requeue) a meaningful `RetryCount` to reset.
