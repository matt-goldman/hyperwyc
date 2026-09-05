# 4. Default to removal

**Status:** Accepted. Generalises
[ADR 0001](0001-idempotency-is-not-hyperwycs-remit.md) from features to implementation.

## Context

Hyperwyc's pitch is that it drops into an existing `HttpClient` pipeline and asks nothing of you.
Its scope test ([ADR 0001](0001-idempotency-is-not-hyperwycs-remit.md)) exists to keep it from
growing responsibilities that belong elsewhere, and it has worked — idempotency, conflict
resolution, duplicate suppression and store recovery have all been declined on its authority.

It has a gap. It asks *should Hyperwyc do this?* Nothing asks *does Hyperwyc need this code?* A
capability can pass the scope test and still be built far heavier than the problem requires, or
built before there was evidence it was needed at all. That is what happened here.

By the time the MAUI sample first proved the core scenario end to end — load a catalogue, go
offline, restart, serve from cache — the core was already carrying a sync orchestrator with retry
budgets, exponential backoff with jitter, persisted retry state, follow-up scheduling, connectivity
debouncing, a dead-letter queue, four cache strategies, a staleness-evaluator abstraction, an
offline-response-policy enum and a hosted service. The proven scenario needs a handler, a store,
and something that reacts to connectivity changes.

The evidence is in the defect record. Of the bugs fixed on this repository —
[28](../../Backlog/Done/28-persisted-retry-state.md),
[29](../../Backlog/Done/29-default-ttl-propagation.md),
[33](../../Backlog/Done/33-orchestrator-sync-disposal.md),
[35](../../Backlog/Done/35-orchestrator-transport-not-injectable.md),
[38](../../Backlog/Done/38-retry-classification.md),
[16](../../Backlog/Done/16-reset-store-async.md),
[51](../../Backlog/Done/51-cabinet-store-not-thread-safe.md) — nearly every one lives in machinery
that is not required to deliver the scenario the sample proved. The TTL bug was the
policy/staleness indirection. The disposal crash was orchestrator lifetime. The stranding bug was
the follow-up scheduler. The flush-gate race was the orchestrator's gate. The handler, the store
and connectivity have been comparatively quiet.

The bugs were not in the product. They were in the scaffolding around it.

## Decision

**Begin every assessment from the premise that the problem is something we added and did not
need.** Not that something is missing; that something is surplus. Move off that premise only once
it is disproven.

Concretely, before proposing an addition — a type, an option, an abstraction, an event, a
configuration knob — first establish what could come out instead. Addition is the move that has to
be justified. Removal is the default.

## Rationale

**A library selling minimalism cannot itself be maximal.** The entire positioning is *slots into
your architecture, asks nothing of you*. Consumers do not audit that claim by reading the README;
they feel it in the size of the surface they have to understand, the options they have to decide
about, and the bugs they hit. Every knob is a claim that the consumer should have an opinion.

**Complexity added before evidence is complexity added against a guess.** Retry budgets and
exponential backoff were built before a single write had been queued and replayed in a real app.
When the real app arrived, the interesting failures were auth, thread safety and store growth —
none of which the guessed-at machinery addressed, and some of which it caused.

**The scope test's own answers were already carrying contradictions.**
[Issue 38](../../Backlog/Done/38-retry-classification.md) concluded that Hyperwyc retries
*connectivity* failures on *connectivity change*. That conclusion needs no retry budget, no
backoff curve, no jitter, no persisted next-attempt time and no timers — yet all of them remained.
[ADR 0002](0002-replays-traverse-the-pipeline.md) established that replays traverse the
application's pipeline, which means the application's own resilience handler already covers the
`5xx` and `429` cases the budget was there for. We had decided against retrying and gone on
carrying the apparatus for it.

That is the pattern this ADR is really about: a decision to *not* do something removes the
justification for code that already exists, and nothing goes back to collect it.

## Consequences

**Deciding not to do something now implies an audit.** When a scope decision is taken, the same
pass asks what existing code that decision has just orphaned. Otherwise the codebase accretes
apparatus for capabilities it has explicitly declined.

**The window is widest before release.** While nothing is published and the only consumers are the
sample and the tests, deletion costs nothing — no migration, no deprecation, no compatibility
shim. That advantage disappears at v1. Audit before, not after.

**Extensibility is the escape hatch, and it is already built.** Removing something does not
foreclose it: `Hyperwyc.Core` plus a consumer-supplied `ISyncStore` is the seam through which
anything deleted can be re-added by whoever actually needs it. That is what makes aggressive
removal safe rather than reckless.

## What the audit actually removed

Recorded here rather than as its own document, because it is not a separate decision — it is what
this one cost and saved when applied.

Recorded because absence is invisible: someone reading this document should not have to work out
from silence that these were considered and taken out.

[ADR 0004](0004-default-to-removal.md) audited the surface against ADR 0001's
scope test and removed everything the project had already decided against but was still carrying.

**All retry apparatus.** `RetryOptions`, `ISyncPolicy.GetRetryOptions`,
`HyperwycOptions.DefaultRetryOptions` and `ConnectivityDebounceDelay`, `Envelope.RetryCount` and
`NextRetryUtc`, `IHyperwycStore.GetReadyToSendAsync`, exponential backoff with jitter, the follow-up
scheduler, the connectivity debounce, `HyperwycEventType.OnRetrying`, and `DeliveryOutcome.AttemptCount`
and `IsFinal`.

[Issue 38](../../Backlog/Done/38-retry-classification.md) had already concluded that Hyperwyc retries
*connectivity* failures on *connectivity change*, and
[ADR 0002](0002-replays-traverse-the-pipeline.md) established that replays
traverse the application's pipeline — so the app's own resilience handler already covers `5xx`
and `429`. The apparatus was serving a responsibility we had declined.

What replaces it: a `4xx` dead-letters on the first attempt; anything else leaves the envelope in
the outbox for the next flush, which happens on connectivity restored or application start. No
budget, no curve, no timers. An envelope can wait indefinitely against a permanently broken
endpoint, which is honest and better than destroying a write.

**`IStalenessEvaluator` and `TtlStalenessEvaluator`.** One implementation, one caller, and the
indirection caused [issue 29](../../Backlog/Done/29-default-ttl-propagation.md). Now a TTL comparison
in the handler. [Issue 41](../../Backlog/41-honour-cacheability-directives.md) is where per-response
staleness earns an interface back.

**`OfflineResponsePolicy`.** The `Signal` mode had no users and asserted the consumer should have
an opinion. Synthetic responses now have one shape: a normal-looking success, distinguished by
`X-Hyperwyc-Status` and — for writes — the `202`.

**`DeliveryOutcome.Headers`.** A full response-header dictionary captured on every outcome that
nothing read.

Nothing here is foreclosed. `Hyperwyc.Core` with a consumer-supplied `IHyperwycStore` is the seam
through which any of it can be re-added by whoever actually needs it, which is what made removing
it safe rather than reckless.

---

## The test this establishes

1. **What can come out?** Ask before asking what goes in. If the answer to a problem is a new
   type, an option or a mechanism, that is the answer requiring justification.
2. **Does a decision we have already taken orphan this?** Declining a responsibility invalidates
   the code that served it. Go and collect it.
3. **Is this removing complexity or relocating it?** Both can be correct —
   [issue 47](../../Backlog/Done/47-connectivity-is-required.md) deliberately moved a decision to
   the consumer, where the knowledge lives. But relocation is not reduction, and calling it one is
   how a surface grows while everyone believes it is shrinking.
4. **Is this machinery around the promise, or the promise?** Durability, header fidelity, buffering
   before a synchronous read: these look like complexity and are the product. Deleting them
   deletes the thing. Everything else is negotiable.

## Held loosely

This is a default, not a conviction, and it is not a position to be defended. It is cheap when
wrong — a removal proposal that turns out to be load-bearing is rejected in one exchange — and
valuable when right, because the common failure is a system that is too complicated rather than
one that is missing a part. Start there; be talked out of it readily.
