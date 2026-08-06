# Issue 34 — Document the Flush Trigger Model (and why shutdown is not one)

## Summary

Document what actually triggers an outbox flush, and state plainly that shutting down or
backgrounding the app is deliberately not among them. No lifecycle integration is required of
consumers.

## Decision

**Hyperwyc does not flush on shutdown, suspension or backgrounding, and does not ask consumers
to wire up lifecycle callbacks.**

An earlier draft of this item proposed documenting a MAUI `OnSleep` → `FlushAsync` pattern.
That is rejected. The reasoning:

**Envelopes are only ever queued because connectivity was poor, and shutting down does not
improve connectivity.** This is structural, not incidental — `HyperwycHandler` writes to the
outbox in exactly one place, `HandleOfflineWriteAsync` (`HyperwycHandler.cs:91`), which is
reachable only when `IConnectivityService.IsConnected` is false. Online writes are sent
immediately and never queued. So a non-empty outbox means the device was offline, and a flush
attempted at shutdown would run under the same conditions that caused the queueing.

The two existing triggers already cover the cases that can actually succeed:

| Trigger | Covers |
|---|---|
| `FlushOnStartup` (default `true`) | App launches while online, including after a previous run was killed mid-flush |
| `IConnectivityService.ConnectivityChanged`, debounced 2s | Connectivity returns while the app is running |

A flush interrupted by the app going away leaves its envelopes queued, and the next launch
picks them up. Nothing is lost; the recovery path is the one that already exists.

### The one genuinely uncovered case

Connectivity returns while the app is suspended. The app runs no code, so no flush occurs until
resume or relaunch. A shutdown flush would not help — it would have run while still offline.
The answer to this is the background sync scheduler already listed under v2.0 in the roadmap,
not lifecycle callbacks.

## Behaviour

Documentation only. No code change, and no new API.

- Document the trigger model above in the README, so the question "do I need to flush on
  sleep?" is answered before it is asked.
- State the reasoning briefly — that queued work implies poor connectivity — because the
  conclusion is unintuitive without it.
- Note that `SyncOrchestrator.FlushAsync` remains available for a manual "sync now" control,
  which is a user-initiated action rather than a lifecycle hook.

## Acceptance Criteria

- [x] README documents the two flush triggers and states that shutdown and backgrounding are
      deliberately not triggers, with the one-line reason.
- [x] The README section currently titled "Lifecycle Events" is renamed (e.g. "Sync Events").
      It documents the `IObservable<SyncEvent>` stream, and a reader looking for
      application-lifecycle guidance currently lands there and is misled.
- [x] README notes that a manual flush is available for a "sync now" affordance.
- [x] TECHNICAL_PLAN §3 states the trigger model explicitly rather than leaving it implied.

## Resolution

README gained a **"When Hyperwyc Syncs"** section stating the two triggers and, under a
subheading a reader will actually find, *"You don't need to hook app lifecycle events"* — with
the reason, because the conclusion is unintuitive without it. It also covers what happens to an
interrupted sync: envelopes stay queued, are not marked failed, and are not dead-lettered. Only
a request the server actually rejected after exhausting its retries reaches the dead-letter
queue.

"Lifecycle Events" is now "Sync Events", with a line pointing at the new section so the two
meanings of "lifecycle" cannot be confused.

TECHNICAL_PLAN's trigger model was already written during issue #33; it now names
`IHyperwyc.FlushAsync` rather than the internal orchestrator.

The net deliverable is a documented *absence* of work for consumers: nothing to wire up, and an
explicit warning against wiring something up anyway.

## Notes

- Consumers needing nothing on shutdown is the desirable outcome, not a limitation: it keeps
  Hyperwyc a drop-in transport concern rather than something the app's lifecycle has to know
  about, which is the whole positioning.
- Related: issue #33 (disposal semantics). This decision simplifies that one — with no flush
  wanted at shutdown, neither disposal path needs to wait for one.
- Related: issue #28 (persisted retry state). Independent of this decision: a flush triggered by
  connectivity restoration can still be cut off by the app being backgrounded, which discards
  the retry budget.
