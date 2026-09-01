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

Run it through [the scope test](../../docs/decisions/README.md#the-standing-scope-test):

| Question | Answer |
|---|---|
| Would this problem exist without Hyperwyc? | **No.** Without it the caller receives the `409` inline and handles it at the call site. The deferral is what hides it |
| Does it require anything of the consumer's API? | **No.** It surfaces what the API already returned |
| Can the application already do this itself? | **No.** Structurally impossible — the response is received by the orchestrator, on a background flush, with no path back to the caller |
| Does it depend on something only Hyperwyc knows? | **Yes, entirely** |

This is the mirror image of [ADR 0001](../../docs/decisions/0001-idempotency-is-not-hyperwycs-remit.md).
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
([issue 23](../23-v1-diagnostics-view.md)) show *why* something failed rather than merely that it
did — its `DeadLetteredItem` record currently carries no failure detail either.

## Success needs this too

A replayed `POST` that succeeds may return the created resource — server-assigned identifiers,
normalised values, a canonical timestamp. The application never sees that response, so it cannot
reconcile its local record with what the server actually stored. `OnSynced` has exactly the same
hole as `OnFailed`.

Less urgent than the failure case, since nothing is broken by ignoring it, but it shares a root
cause and is much better solved once than bolted on afterwards.

## What was built

### Correlation: the caller's key if they have one, ours if they don't

`Envelope.CorrelationId` is taken from `HyperwycRequestOptions.CorrelationId` — an
`HttpRequestOptionsKey<string>` the caller may set — and defaults to `Envelope.Id` when they
don't. It is returned on the synthetic `202` as `X-Hyperwyc-Correlation-Id`, always, whichever
source it came from. One field, two sources, one rule.

`HttpRequestOptions` is local to the message and never transmitted, so this does not breach
ADR 0001. Hyperwyc requires no uniqueness and never deduplicates on the value: it is the
application's key carrying the application's meaning, and several writes deliberately sharing one
is a legitimate thing to want. Kept distinct from `Envelope.Id`, which keys the store and must
stay unique.

### `SyncOutcome`, persisted first

```csharp
public sealed record SyncOutcome
{
    public SyncOutcomeKind Kind { get; init; }        // Succeeded | Rejected | TransientFailure | TransportFailure
    public bool IsFinal { get; init; }                // whether Hyperwyc will try again
    public int? StatusCode { get; init; }
    public string? ReasonPhrase { get; init; }
    public Dictionary<string, string> Headers { get; init; } = [];
    public byte[]? Body { get; init; }
    public bool BodyTruncated { get; init; }
    public string? Error { get; init; }               // transport failures
    public int AttemptCount { get; init; }
    public DateTimeOffset OccurredUtc { get; init; }

    public string? GetBodyAsText();
}
```

Stored on `Envelope.LastOutcome`; the same instance is handed out on `SyncEvent.Outcome`.

`Kind` says what happened, `IsFinal` says what Hyperwyc will do next, and together they give the
"rejected outright vs budget exhausted" distinction — `Rejected` + `IsFinal` is the server
refusing, `TransientFailure` + `IsFinal` is the budget running out. That pairing is Background
Sync's `event.lastChance` under another name, which is some reassurance it is the right cut.

### `SyncEvent`

```csharp
public record SyncEvent(
    SyncEventType Type,
    string Url,
    string Method,
    DateTimeOffset Timestamp,
    string? CorrelationId = null,
    string? RequestId = null,
    string? RequestBody = null,
    SyncOutcome? Outcome = null);
```

Optional parameters, so the existing four-argument construction still compiles.

## Decisions on the open questions

**1. How the caller learns the correlation id — both, with the caller's value winning.**

The design sketch proposed only `X-Hyperwyc-Request-Id` on the `202`. The prior art points the
other way: Workbox attaches arbitrary `metadata` to a queue entry and Background Sync uses a
caller-chosen tag. Both use *caller-supplied* identity rather than minting one and handing it
back, and it is better — an application wants to correlate on `sale.Id`, which it already has,
not on a Hyperwyc GUID it must keep a mapping table for.

The objection was ergonomic: `HttpRequestOptions` needs an `HttpRequestMessage`, so the JSON
convenience methods can't reach it. That objection dissolves once the two are one field rather
than two mechanisms. Hyperwyc is a `DelegatingHandler`, so it sees an `HttpRequestMessage`
however the caller built it — read the option if present, generate if not. Supplying your own
becomes the price of an upgrade rather than the price of admission, and the header still closes
the loop for callers who did nothing.

Renamed from `X-Hyperwyc-Request-Id`: `Request-Id` implies Hyperwyc minted it, which is now only
sometimes true.

**2. Body capture — a separate, much smaller cap, clipped rather than dropped.**

`MaxOutcomeBodyBytes`, default 16 KB, against `MaxCachedResponseBodyBytes`' 512 KB. Different
job: that one sizes a payload being cached for later reads, this one sizes an explanation of why
a write failed and is persisted per dead-lettered envelope. Clipping with `BodyTruncated` because
half an error message is still actionable and a missing one is not. Read from the stream rather
than via `ReadAsByteArrayAsync`, so a pathological body is never fully buffered just to be
discarded. Zero disables capture. A failed read yields no body rather than failing the flush.

**3. Success outcomes are event-only.**

Prior art agrees — Workbox and Background Sync both simply remove the entry, retaining nothing.
A delivered envelope leaves the outbox, so persisting its outcome would mean a "recently
completed" table with its own growth and eviction problem, which is issue 42's shape of work for
a much weaker reason.

The hole is real and documented: an app killed mid-flush loses the server's canonical record. It
also passes the scope test in the other direction — an application that needs certainty can
re-read the resource, which is something it can already do.

**4. `OnRetrying` carries the same outcome shape.**

With the body capped small this is a bounded read on a response already in hand, and the envelope
is already being written on defer for `RetryCount`/`NextRetryUtc`. Asymmetric capture would be a
rule to document, a rule to test, and a rule someone gets wrong later. `OnRetrying` reports the
*previous* attempt's outcome, since it fires before the current attempt is made — which is
exactly what "why is this being retried" wants.

**5. No `Exception`; a `Kind` plus a `string? Error`.**

Not really an open question once the record is persisted first: an exception does not round-trip
through a document store. Carrying one on the event while the stored copy held a string would
mean two shapes for the same fact. The exception type is also an implementation detail of
whichever transport is in use, and not worth making part of the public contract.

## Decisions not in the original list

**No `ISyncStore` change.** `MoveToDeadLetterAsync` sets a flag on the stored envelope in place,
so persisting the outcome is `UpsertAsync` followed by the existing move. The two writes are not
atomic; a crash between them leaves the envelope carrying its outcome but still pending, so it is
retried and — classification being deterministic on the status code — reaches the same verdict.
One retry that should have been terminal, on a crash, is a better trade than a breaking change to
a public interface.

**A transport failure is recorded but publishes no event.** The flush abandons and the envelope
keeps its place, so no lifecycle transition has occurred and no existing event type fits. The
persisted outcome is what lets a diagnostics view explain an outbox that will not drain. It does
not charge the retry budget, per issue 38, so `AttemptCount` is unchanged by it.

**The outcome body is `byte[]` ahead of [issue 25](25-binary-request-response-bodies.md).**
Envelope bodies are still strings until 25 lands, so this is briefly inconsistent — but it is the
direction everything moves in, and it is one declaration now against a second migration later.
`GetBodyAsText()` covers the common case. 25 was considered for promotion ahead of this and
rejected: this is the v0.1 blocker, 25 is v1.0, and with no `ISyncStore` change needed the only
coupling was that one type.

## What the prior art was worth

Recorded because "check Service Worker first" is a standing instruction and this is a data point
on how much it pays.

- **Background Sync doesn't take custody of requests** — you store them yourself and `fetch` them
  in your own handler, holding the `Response`. So the problem this issue exists for never arises
  there. Useful to know it is a consequence of Hyperwyc's design choice, not an oversight.
- **Workbox's `Queue` does take custody, and handles this worse than we already did.** Its
  `replayRequests` only treats a *thrown* fetch as failure, so an HTTP error response — `409`,
  `500`, anything — counts as delivered and the entry is silently dropped. Issue 38's
  classification is ahead of the reference model here.
- **The client-messaging problem is one they genuinely solved, and it is ours exactly.** A service
  worker often runs with no page open, so `postMessage` goes nowhere; the established pattern is
  to write the outcome to IndexedDB and let the next page load read it. That is a direct
  confirmation of "events are necessary but not sufficient", and it produced the ordering used
  here: **design the persisted record first and derive the event from it**, rather than designing
  the event and asking afterwards what to store.
- **`event.lastChance`** is `IsFinal` under another name.

Net: one design principle adopted (persist first), one API shape changed (caller-supplied
correlation), one assumption checked and found to be ahead of the model. Worth the look.

## Acceptance Criteria

- [x] `SyncEvent` carries enough to identify which queued operation the event concerns.
- [x] `OnFailed` carries the status code, reason phrase and response body where there was a
      response. A transport failure is recorded on the envelope rather than published — see
      above.
- [x] `OnFailed` distinguishes "rejected outright" from "retry budget exhausted".
- [x] `OnSynced` carries the response for a replayed request, so a caller can reconcile.
- [x] Failure detail is persisted on the envelope, so a dead-lettered record explains itself
      after a restart.
- [x] Response body capture respects a documented size limit.
- [x] Decision recorded on each open question above.
- [x] Unit test: a `409` on a replayed write surfaces the status and body to a subscriber.
- [x] Unit test: the event identifies which of several queued writes to the same URL failed.
- [x] Unit test: a transport failure surfaces as such, distinguishably from an HTTP error.
- [x] Unit test: failure detail survives serialisation — the envelope a durable store is handed
      round-trips through JSON with its outcome intact. A true end-to-end restart assertion needs
      a read path for dead-lettered envelopes, which is [issue 23](../23-v1-diagnostics-view.md).
- [x] README documents how to respond to a deferred failure, framed as "here is what you are
      given", not "here is what to do with it".
- [x] TECHNICAL_PLAN §6 updated with payloads, correlation and the `SyncOutcome` design.
- [x] README's "once sync-status inspection lands" caveat removed.
- [x] [Issue 23](../23-v1-diagnostics-view.md) updated: `DeadLetteredItem` should carry the failure
      detail this issue persists.

## Still open

- **Binary bodies.** `Envelope.RequestBody` and `CachedResponse.Body` remain strings until
  [issue 25](25-binary-request-response-bodies.md). `SyncOutcome.Body` is already `byte[]`.
- **No read path for dead-lettered envelopes.** The failure detail is persisted and correct, but
  nothing can enumerate it yet; that is [issue 23](../23-v1-diagnostics-view.md), which should now
  surface `LastOutcome` on its `DeadLetteredItem`.

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

- Raised while building the sample MAUI app ([issue 19](../Done/19-poc-maui-app.md)), whose specification
  includes a per-sale "Failed" badge — not currently implementable, since an event cannot be
  attributed to a specific sale.
- P0 rather than v1.0: this is a completeness gap in the core proposition rather than an
  enhancement. A library that accepts responsibility for delivering a write later, and then
  cannot tell you it did not, is incomplete regardless of how narrow its scope is.
- Interacts with [issue 25](25-binary-request-response-bodies.md): response bodies are strings
  today, so a binary error body would be mangled here in the same way. Worth landing 25 first, or
  at least designing the outcome type against `byte[]` from the start.
