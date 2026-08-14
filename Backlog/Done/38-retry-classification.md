# Issue 38 — Retry Model: Connectivity-Driven, Not Time-Driven

## Summary

`SyncOrchestrator` runs a generic time-based retry loop inside each flush — 5 attempts with
exponential backoff, on any non-2xx. That is not Hyperwyc's job. Hyperwyc exists to get writes
delivered once connectivity allows it, so a failed attempt should leave the envelope queued for
the *next connectivity opportunity*, not grind through a backoff curve in place.

## The single-responsibility argument

Hyperwyc's responsibility is not "retry failures". It is narrower on two axes:

- **Which failures:** connectivity ones. A write is in the outbox precisely because the network
  was unavailable. That is the failure Hyperwyc owns.
- **When:** when connectivity state has changed. Retrying at a moment when nothing has changed is
  guessing; retrying when the network came back is responding to actual new information.

Everything else belongs to someone else. An application that wants to retry a `503`, refresh a
token on `401`, or trip a circuit breaker has handlers for that, and — because Hyperwyc's retry
wraps the whole `HttpClient` pipeline — those handlers run *first*, so Hyperwyc only ever sees
what they could not resolve. Generic retry here is duplicated work at the wrong layer.

## The interface already models this

`ISyncStore` and `Envelope` were designed for exactly this, and the orchestrator diverged:

```csharp
public int RetryCount { get; set; }                    // Envelope
public DateTimeOffset? NextRetryUtc { get; set; }      // Envelope
Task<IReadOnlyList<Envelope>> GetDueForRetryAsync(DateTimeOffset now, ...);   // ISyncStore
```

These only make sense if a failure *schedules* a later attempt that a subsequent pass collects.
They are meaningless inside a Polly loop, where every attempt happens within one call and nothing
is ever scheduled — which is why all three have been dead since they were written.

This answers the open question in [issue 28](28-persisted-retry-state.md), which framed them as
"persist them, or delete them as dead surface". Neither: they are the correct model, orphaned by
an implementation that went another way.

## Behaviour

| Outcome | Action |
|---|---|
| Success | Mark synced, publish `OnSynced` |
| `4xx` | Dead-letter immediately, publish `OnFailed` — permanent, so no attempt is wasted on it |
| `5xx` | Leave queued, increment `RetryCount`, set `NextRetryUtc`, move to the next envelope |
| Transport exception | Connectivity is gone — **abort the flush**, leave everything queued |
| `RetryCount` exceeds budget | Dead-letter, publish `OnFailed` |

### A transport failure should end the flush, not the envelope

If `HttpRequestException` is thrown, the premise of the flush — that the device can reach the
network — has been falsified. The remaining envelopes will fail for the same reason. Continuing
to send them into a dead network wastes battery and radio, and the failures carry no information.
Abandoning the flush and waiting for the next connectivity signal is faster and more truthful
about what is actually known.

This is the case where `IConnectivityService` reports online but the network is not usable —
captive portals, weak signal, DNS failure.

### `4xx` is permanent at the point Hyperwyc observes it

Because Hyperwyc's retry is outermost, a `4xx` reaching it has already survived whatever the
application does about such things:

| Application setup | What Hyperwyc sees on a `401` | Right action |
|---|---|---|
| A refresh-on-401 handler in the pipeline | Only a `401` the refresh attempt *failed* to fix | Dead-letter — Hyperwyc cannot do better |
| No refresh mechanism | A `401` nothing will resolve | Dead-letter — retrying changes nothing |

Classifying `4xx` as permanent is therefore deference, not prescription. The same reasoning
covers `403`, `409`, `422` and the rest.

## The backend-having-a-bad-day case

Hyperwyc's primary use case is mobile offline, but an application whose backend is temporarily
unhealthy is a real scenario and the model has to answer it.

Under connectivity-driven retry alone, a device that stays online while the server returns `503`
gets one attempt per flush trigger — so if connectivity never changes and the app is not
restarted, the write waits. Nothing is lost; it is durable and will go. But "waits until next
launch" is a poor answer to an outage that resolves in five minutes.

Options, to decide during implementation:

1. **Existing triggers only.** `NextRetryUtc` acts as a "not before" guard within a connectivity
   episode. Simplest; leaves the gap above.
2. **A self-scheduled follow-up pass.** When a flush leaves envelopes deferred, the orchestrator
   schedules one delayed re-flush. Needs no consumer wiring, closes the common case, and is a
   modest amount of machinery.
3. **A periodic scheduler.** The full answer, already on the roadmap for v2.0 as background sync.

Option 2 looks like the right size for v0.1: it handles a transient backend problem without
introducing a general scheduler, and degrades to option 1 when the app is backgrounded.

## What this dissolves

Problems currently documented separately that stop existing under this model:

- **Queue blocking.** A permanently-failing write costs one attempt rather than ~62 seconds, so
  it no longer starves the writes behind it. This was the original motivation for this issue.
- **Multiplicative retries.** With no in-flush loop, an application's own retry handler has
  nothing to nest inside. The README warning about compounding attempts can go.
- **`Retry-After` handling, jitter, backoff tuning.** Largely moot once attempts are spaced by
  connectivity events rather than by a curve.
- **The `Polly` dependency.** Its only use in `Hyperwyc.Core` is `BuildRetryPipeline`. Computing
  a next-attempt time is arithmetic; it does not need a resilience library.

## Open Questions

1. **Drop `Polly` from `Hyperwyc.Core`?** Nothing else uses it. Against: it is a well-regarded
   dependency and some future need might reintroduce it. For: a core library aimed at mobile
   should carry what it uses, and backoff arithmetic is a few lines. Worth deciding deliberately
   rather than letting it fall out of the refactor.
2. **Does a permanent failure deserve its own event type?** `OnFailed` currently means "retries
   exhausted". Reusing it for "rejected outright" is defensible — both mean the write will not be
   delivered — but a consumer showing a "Failed" badge might want to distinguish "the server said
   no" from "we could not reach the server". Sharper now that the two are genuinely different
   code paths rather than two ends of one loop.
3. **Should the classification be configurable?** Suggest not, initially. Most applications want
   the standard behaviour, and a knob here invites misconfiguring the mechanism that protects the
   queue.

## Acceptance Criteria

- [x] In-flush retry loop removed; one attempt per envelope per flush.
- [x] `4xx` dead-letters on the first attempt without consuming the budget.
- [x] `5xx` increments `RetryCount`, sets `NextRetryUtc`, and leaves the envelope queued.
- [x] A transport exception aborts the remainder of the flush.
- [x] `RetryCount` exceeding the configured budget dead-letters.
- [x] The flush query is used, so the three orphaned members become live — renamed `GetReadyToSendAsync`, see below.
- [x] Decision recorded on the backend-outage options above.
- [x] Decision recorded on dropping `Polly`.
- [x] Unit test: a `409` dead-letters on the first attempt.
- [x] Unit test: a `503` leaves the envelope queued with `RetryCount` incremented, and does not
      delay the next envelope in the flush.
- [x] Unit test: a permanently-failing envelope does not delay a good one behind it — the
      queue-blocking behaviour is why this matters, so it should be pinned.
- [x] Unit test: a transport exception stops the flush, leaving later envelopes untouched.
- [x] Unit test: the retry budget survives across orchestrator instances, now that it is persisted.
- [x] `POC.md`'s "Known rough edges" entry about `409` retries removed.
- [x] README's stacking-retries warning removed, and the pipeline-composition section revisited —
      the reasoning stands, but the multiplication no longer happens.
- [x] TECHNICAL_PLAN §3 rewritten: its retry description documents the current loop.

## Resolution

Implemented. `SendWithRetryAsync` and `BuildRetryPipeline` are replaced by a `SendAsync` that
makes one attempt and returns a `SendOutcome` — `Synced`, `DeadLettered`, `Deferred` or
`ConnectivityLost` — which the flush loop acts on.

**Decisions taken:**

- **`GetDueForRetryAsync` renamed to `GetReadyToSendAsync`** and widened to include
  never-attempted envelopes. A method returning envelopes that have never been retried should
  not be called "due for retry", and `ISyncStore` is implemented by third parties, so the name
  has to be honest. `GetPendingOutboxAsync` keeps its broader meaning and becomes the
  diagnostics query.
- **Backend-outage handling: option 2**, a self-scheduled follow-up pass. When a flush defers
  anything, one further flush is scheduled for when the earliest deferred envelope comes due.
  No consumer wiring, and it terminates because deferrals eventually exhaust the budget.
- **`Polly` dropped from `Hyperwyc.Core`.** It existed solely for `BuildRetryPipeline`.
- **A zero `InitialDelay` is now honoured as zero.** The old code substituted 1ms because Polly
  required a positive delay; scheduling "eligible immediately" is perfectly valid, and treating
  a configured zero as something else was a workaround for a constraint that no longer exists.

**The follow-up pass had a stranding bug, caught by its own test.** The first implementation
scheduled the next pass from what the current flush had deferred. `Task.Delay` can fire a
millisecond or two early, so the follow-up would find nothing ready, defer nothing, and
therefore schedule nothing further — leaving the envelope stranded until the next connectivity
change. Scheduling is now derived from what is *still waiting in the store*, which is correct
regardless of why a pass deferred nothing, plus a small buffer past the due time so near-misses
do not cost an extra round-trip. Verified over five consecutive suite runs.

**Two things fixed in passing**, both consequences of the rewrite rather than separate work:

- Responses are now disposed. The old path never disposed them, which leaks connections back to
  the pool more slowly than it should.
- The per-envelope "probe request" built only to ask the policy for retry options is gone; the
  actual request serves that purpose.

**A test had to be rewritten, not just updated.** `FlushAsync_SucceedsOnRetry_PublishesOnRetryingThenOnSynced`
asserted the in-flush behaviour — fail, retry, succeed, all within one flush. Under this model
the retry is a *later* flush, so it now covers the whole path end to end: first attempt defers,
the follow-up pass fires, the second attempt succeeds. That exercises the scheduling machinery
the old test could not have.

## Notes

- **Supersedes this item's original scope**, which was narrower: classify responses so a `409`
  stops burning its budget. That fix was correct but treated the symptom. The cause is that
  Hyperwyc is doing generic retry at all.
- **Absorbs [issue 28](28-persisted-retry-state.md).** That item asked whether to persist retry
  state or delete it as dead surface; this model requires persisting it, so 28 should close in
  favour of this once implemented.
- Raised while building the sample API ([issue 18](18-poc-web-api.md)), where overselling a
  product returns `409` and is the natural way to demonstrate dead-lettering — and currently takes
  about a minute to do it.
- `catch (HttpRequestException) { }` in `SendWithRetryAsync` currently swallows the exception.
  Under this model that exception is the signal to abort the flush, so it can no longer be
  discarded.
- The primary use case remains mobile offline. The backend-outage path is an edge case, but a
  real one: an application whose API is having its own problems is still an application whose
  writes should survive.
