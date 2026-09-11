# Issue 39 — Stop Injecting Idempotency-Key: Idempotency Is Not Hyperwyc's Remit

## Summary

We previously added idempotency key injection (see [08](08-idempotency-key-injection.md)) to make cache flushes safe. However, this is problematic as it's antithetical to one of Hyperwyc's core principles.

The decision: **Hyperwyc stops injecting the header and is naive about idempotency entirely.**
Duplicate suppression is a concern between the application and its API, and Hyperwyc has no
business having an opinion about it.

## Background

Hyperwyc exists in part to avoid approaches required by other offline sync libraries. Things like Realm or Azure Mobile Apps require you to architect your whole infrastructure and solution around the requirements of one sync library; Hyperwyc by contrast is intended to slot in invisibly to your existing architecture. The idempotency key header violates this as it requires you to update your API to be Hyperwyc aware. This is the opposite of the intent.

### It is not a problem Hyperwyc creates

Duplicate delivery is a property of *retrying*, not of Hyperwyc. A Polly retry handler, a user
double-tapping a button, or a proxy replaying a request all produce the same outcome: the API
receives the same logical operation twice. APIs cope with that or they do not, and application
code interprets whatever comes back — a conflict, a bad request, a duplicate record.

Hyperwyc's retry carries exactly this risk and no more. It is the same class of problem, with
the same owners, and it exists whether or not Hyperwyc is in the picture. Solving it here means
solving a general HTTP concern inside a library whose remit is narrower than that.

The standing of the header does not change this. `Idempotency-Key` is an
[IETF HTTP API working group draft](https://datatracker.ietf.org/doc/draft-ietf-httpapi-idempotency-key-header/)
implemented by Stripe, Square and others, so an API honouring it is conventionally
idempotency-aware rather than Hyperwyc-aware. That makes the header respectable; it does not
make emitting it Hyperwyc's job. Being a standard is an argument for it being *available*, not
for it being *automatic*.

### The sample demonstrates the cost

`Shared.Sale` already carries a client-generated identity. Despite that, the sample API grew an
idempotency-key dictionary and a lookup on every `POST /sales`, existing *solely* because
Hyperwyc sends the header. Backend code was written to satisfy the library while the domain
already had everything needed. That is the imposition, demonstrated on our own sample.

## The mechanism already exists

Nothing needs building for consumers who *do* want an idempotency key. `Envelope.ForRequest`
captures the request's headers, and `SyncOrchestrator.BuildRequest` replays them verbatim, so a
header set by application code at the call site is carried through every replay unchanged:

```csharp
request.Headers.Add("Idempotency-Key", sale.Id.ToString());
```

The call site is the right place for this, because that is where "this is one logical operation"
is actually known. Hyperwyc's part is simply to not interfere — which is what removing the
injection achieves.

## Behaviour

1. **Stop injecting `Idempotency-Key`.** `HyperwycHandler` no longer adds it; `SyncOrchestrator`
   no longer re-adds it on replay. Hyperwyc sends the request the application made.
2. **Decouple `Envelope.Id` from the header.** `ForRequest` currently derives its `Id` by reading
   `Idempotency-Key`. The envelope should own its identity outright.
3. **An application-set `Idempotency-Key` is persisted and replayed verbatim**, exactly like any
   other header. This already works; it needs a test so it cannot regress.
4. **Document the concern without prescribing an answer.** If duplicate writes matter to a
   consumer, some approaches people use:
   - Client-generated domain identity, so the write is idempotent by construction.
   - The `Idempotency-Key` header, set at the call site, for backends that implement it.
   - A correlation or transaction ID the application already emits — common in event-driven
     systems, and increasingly generated in the UI so analytics can be cross-referenced with
     backend telemetry.

   Examples of what people do, explicitly not a recommendation from Hyperwyc. The framing should
   be "if this matters to you, here is the shape of the problem", not "here is what you should do".

## Non-goals

- **No opt-in injection option.** A flag would only re-establish the header as the blessed
  answer, in a library that has just concluded it should not have one. Consumers who want it
  write one line at the call site.
- **No `RequestId` exposure.** An earlier draft of this issue proposed surfacing the envelope ID
  on request options so a consumer's *handler* could stamp a stable key. That was reasoning about
  the wrong layer: a handler cannot tell a replay from a first attempt, but the call site can and
  already sets whatever it likes. Exposing an ID for diagnostics may still be worth doing, but
  that belongs with [issue 23](23-v1-d0agnostics-view.md), not here.

## Acceptance Criteria

- [x] `HyperwycHandler` no longer injects `Idempotency-Key`.
- [x] `SyncOrchestrator` no longer re-injects it on replay.
- [x] `Envelope.Id` is generated independently of any request header.
- [x] Unit test: a request the application did not put an `Idempotency-Key` on reaches the
      transport without one, on both the direct and replay paths.
- [x] Unit test: an application-set `Idempotency-Key` is persisted and replayed with the same value.
- [x] `IdempotencyKeyTests` rewritten — it currently asserts the injection behaviour throughout.
- [x] README documents duplicate delivery as a general retry concern with several approaches,
      none of them presented as Hyperwyc's recommendation.
- [x] TECHNICAL_PLAN §2 updated — it currently describes injection as core behaviour, including
      in the numbered write-path steps.
- [x] ROADMAP's shipped list updated — it lists "Idempotency-Key header injection" as a v0.1 feature.
- [x] Sample updated to whatever approach it chooses to demonstrate, with its idempotency-key
      dictionary removed if it is no longer the mechanism in play.
- [x] POC.md's idempotency section rewritten accordingly.
- [x] [Issue 08](08-idempotency-key-injection.md) annotated as superseded, with the reasoning,
      so the original decision is not simply erased.

## Resolution

Removed. `HyperwycHandler` no longer injects, `SyncOrchestrator` no longer re-injects, and
`Envelope.Id` is a plain GUID that is never sent — internal bookkeeping with no meaning to
anyone's API.

**The sample needed almost nothing.** `SalesService.RecordSale` already deduplicated on
`Sale.Id`, the client-generated domain identity, so the sample was demonstrating the right thing
before the library caught up. Only a stale description string on the root endpoint had to change.
That is the argument closing the loop: the domain had the answer all along, and the header was
asking the backend for something it did not need to provide.

**`IdempotencyKeyTests` became `RequestHeaderFidelityTests`**, since every test in it asserted
the removed behaviour. The replacement pins the inverse — nothing is added on the direct or
replay path, and `Envelope.Id` is not adopted from a caller's header — plus the property that
actually matters to consumers: a key set at the call site survives queueing and *every* replay
attempt with the same value. That last test failing would mean application-level idempotency
had silently broken.

**A stale claim elsewhere was corrected in passing.** TECHNICAL_PLAN §1 still warned that an
application retry handler "nests inside Hyperwyc's, multiplying total attempts". That stopped
being true with [issue 38](38-retry-classification.md) — a flush makes one attempt per
envelope, so an application's retry composes within it rather than compounding.

## Notes

This issue is largely a placeholder for now and will get fleshed out. The requirement here is to reconsider, meaning we need to record a decision about how we proceed. My current thinking is that idempotency is already a domain concern, and working through the sample app the simplest approach is for the Sale ID in the client to be the source of truth. Many applications depend on the database in the back end to generate an ID, but shifting that to the client resolves the whole thing. This can be recorded in the docs/guidance as a recommendation, not a prescription, and it's up to consumers to do with this what they will. But also note that it's not the only approach, many solutions, especially those using event driven architecture (or any event based logic) already use a correlation ID or some specific identifier for the transaction/interaction itself. Increasingly this is generated by the UI so that UI analytics can be cross-referenced with back-end telemetry, so there's already precedent.

As mentioned this needs consideration, but I think the consideration is around guidance rather than what's built-in to Hyperwyc. The current idempotency key header is too prescriptive and dictates API changes which Hyperwyc has no business touching or mandating.

---

- **Renumbered from 33 to 39.** 33 belongs to
  [orchestrator disposal](33-orchestrator-sync-disposal.md); numbers are permanent and not
  reused once assigned.
- Consistent with [issue 38](38-retry-classification.md) (retry) and
  [issue 37](37-replay-through-pipeline.md) (auth): in each case Hyperwyc does the narrow
  thing only it can do and stays out of concerns the application already owns. Idempotency is a
  third instance of the same principle, and the clearest one — here Hyperwyc's correct
  contribution is nothing at all.
