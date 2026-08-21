# Issue 40 — Surface the Outcome of a Deferred Request

## Summary

When a queued write is eventually delivered — or eventually rejected — the application cannot see
what happened. `SyncEvent` carries only the event type, URL, method and a timestamp, so a
consumer learns *that* something failed at a URL and nothing else. The status code, the response
body, and which queued operation it was are all discarded.

## The problem

A field engineer, underground and offline, orders 60 replacement bucket teeth. Hyperwyc queues
the write and returns `202 Accepted`; the app shows the order as recorded. Hours later the device
catches a signal, the write replays, and the server responds `409` with:

```json
{ "error": "Only 20 of 'Bucket Tooth, Class 40' left in stock.", "available": 20 }
```

The engineer now needs to decide: reduce the order to 20, or back-order the rest. The application
has to prompt them. It cannot, because all it receives is:

```
OnFailed, "https://api.example.com/sales", "POST", 2026-08-14T09:12:33Z
```

Three separate pieces are missing:

1. **Why it failed.** `409` and `400` and `429` are indistinguishable, as is a transport failure.
2. **What the server said.** The response body — which is where "only 20 available" lives — is
   read by the orchestrator and discarded.
3. **Which operation it was.** With several sales queued to `/sales`, URL and method do not
   identify one. This is the sharpest gap: without it the other two cannot be acted on, because
   the application cannot map the failure onto its own record.

## Why this is Hyperwyc's responsibility

Run it through [the scope test](../docs/decisions/README.md#the-standing-scope-test):

| Question | Answer |
|---|---|
| Would this problem exist without Hyperwyc? | **No.** Without it the caller receives the `409` inline and handles it at the call site. The deferral is what hides it |
| Does it require anything of the consumer's API? | **No.** It surfaces what the API already returned |
| Can the application already do this itself? | **No.** Structurally impossible — the response is received by the orchestrator, on a background flush, with no path back to the caller |
| Does it depend on something only Hyperwyc knows? | **Yes, entirely** |

This is the mirror image of [ADR 0001](../docs/decisions/0001-idempotency-is-not-hyperwycs-remit.md).
There, the correct contribution was nothing at all; here it is the one thing no one else can do.

Hyperwyc's bargain with a caller is *"take this `202`, I will take responsibility for delivering
it."* Accepting that responsibility entails reporting the outcome. Returning success and then
silently dead-lettering is not a narrow scope, it is an incomplete one — the README already
concedes the debt, promising that "once sync-status inspection lands, callers will be able to
confirm the eventual outcome".

**Surface, do not prescribe.** Hyperwyc's job is to hand back enough information for the
application to do whatever is right for its domain — prompt, auto-reduce, back-order, escalate,
discard. It should have no opinion on which.

## Events are necessary but not sufficient

`IObservable<SyncEvent>` is transient. A mobile app is routinely killed between a background
flush and the user next opening it, so a failure delivered only as an event is a failure the user
never hears about.

The outcome must therefore also be **persisted on the envelope**, so a dead-lettered record can
still explain itself hours later. That is also what would let the diagnostics view
([issue 23](23-v1-diagnostics-view.md)) show *why* something failed rather than merely that it
did — its `DeadLetteredItem` record currently carries no failure detail either.

## Success needs this too

A replayed `POST` that succeeds may return the created resource — server-assigned identifiers,
normalised values, a canonical timestamp. The application never sees that response, so it cannot
reconcile its local record with what the server actually stored. `OnSynced` has exactly the same
hole as `OnFailed`.

Less urgent than the failure case, since nothing is broken by ignoring it, but it shares a root
cause and is much better solved once than bolted on afterwards.

## Design sketch

To be settled during implementation; the shape below is a starting point, not a decision.

A nullable composite on `SyncEvent` rather than a spread of nullable scalars, since most fields
are meaningless for `OnQueued`:

```csharp
public record SyncOutcome(
    int? StatusCode,                                  // null for a transport failure
    string? ReasonPhrase,
    string? Body,                                     // subject to a size cap
    IReadOnlyDictionary<string, string> Headers,
    Exception? Exception,                             // transport failures only
    int AttemptCount,
    bool IsPermanent);                                // rejected outright vs budget exhausted
```

And on the event, enough to correlate:

```csharp
public record SyncEvent(
    SyncEventType Type,
    string Url,
    string Method,
    DateTimeOffset Timestamp,
    string? RequestId = null,        // Envelope.Id — identifies the queued operation
    string? RequestBody = null,      // lets the consumer deserialise its own object
    SyncOutcome? Outcome = null);
```

`RequestId` being exposed does not conflict with ADR 0001. That decision was that the envelope id
is never sent *to the server*; handing it to the local caller is a different matter, and is what
makes correlation possible.

## Open Questions

1. **How does the caller learn `RequestId` at queue time?** Correlating by request body works but
   makes the consumer parse its own payload back out. Returning the id on the synthetic `202` —
   an `X-Hyperwyc-Request-Id` header alongside `X-Hyperwyc-Status` — would let an application
   record the association when it queues, which is the natural moment. Adds a second response
   header; that header is on a response Hyperwyc generates, so it imposes nothing on any API.
2. **How much response body to capture?** Error bodies are usually small, but nothing guarantees
   it. Reuse `MaxCachedResponseBodyBytes`, introduce a separate smaller cap, or truncate with a
   marker. Note the body must be read before the response is disposed, so this is a cost paid on
   every failed attempt.
3. **Should the outcome be persisted for successes as well**, or only failures? A successful
   envelope is removed from the outbox, so there is nowhere natural to keep it — which may mean
   success is event-only, and reconciliation is inherently best-effort if the app was not running.
4. **Does `OnRetrying` carry an outcome too?** It would explain *why* a retry is happening, which
   is useful in a diagnostics log, at the cost of capturing bodies on every transient failure.
5. **Is `Exception` the right thing to hand out?** It is the most informative and the least
   stable. A string message plus a category may serve consumers better than an exception object
   whose type is an implementation detail of the transport.

## Acceptance Criteria

- [ ] `SyncEvent` carries enough to identify which queued operation the event concerns.
- [ ] `OnFailed` carries the status code, reason phrase and response body where there was a
      response, or the transport failure detail where there was not.
- [ ] `OnFailed` distinguishes "rejected outright" from "retry budget exhausted".
- [ ] `OnSynced` carries the response for a replayed request, so a caller can reconcile.
- [ ] Failure detail is persisted on the envelope, so a dead-lettered record explains itself
      after a restart.
- [ ] Response body capture respects a documented size limit.
- [ ] Decision recorded on each open question above.
- [ ] Unit test: a `409` on a replayed write surfaces the status and body to a subscriber.
- [ ] Unit test: the event identifies which of several queued writes to the same URL failed.
- [ ] Unit test: a transport failure surfaces as such, distinguishably from an HTTP error.
- [ ] Unit test: failure detail survives a restart — a new orchestrator over the same store can
      still report why an envelope was dead-lettered.
- [ ] README documents how to respond to a deferred failure, with the back-order scenario or
      similar, framed as "here is what you are given", not "here is what to do with it".
- [ ] TECHNICAL_PLAN §6 updated — the event table describes only the trigger, not the payload.
- [ ] README's "once sync-status inspection lands" caveat removed or narrowed.
- [ ] [Issue 23](23-v1-diagnostics-view.md) updated: `DeadLetteredItem` should carry the failure
      detail this issue persists.

## A framing note for the documentation

Worth a passing mention when this is written up, not a section of its own.

Surfacing deferred outcomes nudges consumers toward thinking about their application the way
distributed systems are already thought about: the affordance is "order **submitted**", not
"order **successful**", with the outcome arriving separately and later. Most applications
building on Hyperwyc will already be doing this in their backend without necessarily having
carried the idea into the client.

It is useful framing for some readers and unnecessary for others, so it belongs as a footnote
rather than an argument — Hyperwyc does not require anyone to model their UI a particular way.

## Notes

- Raised while building the sample MAUI app ([issue 19](19-poc-maui-app.md)), whose specification
  includes a per-sale "Failed" badge — not currently implementable, since an event cannot be
  attributed to a specific sale.
- P0 rather than v1.0: this is a completeness gap in the core proposition rather than an
  enhancement. A library that accepts responsibility for delivering a write later, and then
  cannot tell you it did not, is incomplete regardless of how narrow its scope is.
- Interacts with [issue 25](25-binary-request-response-bodies.md): response bodies are strings
  today, so a binary error body would be mangled here in the same way. Worth landing 25 first, or
  at least designing the outcome type against `byte[]` from the start.
