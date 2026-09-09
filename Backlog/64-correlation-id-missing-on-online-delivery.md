# Issue 64 — A Correlation Id the Caller Set Is Missing From `OnDelivered` on the Online Path

## Summary

`HyperwycRequestOptions.CorrelationId` lets a caller name a write so its outcome can be matched
back to a local record. It works for a queued write. It does not reach the `OnDelivered` published
when a write goes out online, which is constructed with four arguments and no correlation id at
all:

```csharp
// HyperwycHandler.HandleOnlineWriteAsync
_events.Publish(new HyperwycEvent(
    HyperwycEventType.OnDelivered, url, request.Method.Method, DateTimeOffset.UtcNow));
```

So an application driving its UI from the event stream has a hole in the happy path: every
deferred write is correlatable and every immediately-delivered one is not.

## Status

💭 Under consideration. Filed 2026-09-08, found while doing
[58](Done/58-docs-code-reconciliation.md) — `docs/events.md` claimed "every event carries a
`CorrelationId`", which is what sent me looking.

## Is it actually a problem?

Arguable both ways, which is why this is a question rather than a defect.

**Against fixing it.** A write that goes out online returns the real response synchronously. The
caller already knows what happened, at the call site, with the correlation id in hand — it never
needed an event. The event stream exists for outcomes that arrive *after* the caller has moved
on, and this outcome did not.

**For fixing it.** The premise of the design is that the caller does not have to know which path a
request took: an offline write and an online one look the same going in, and the `202` versus the
real response is the only difference coming out. An application that decided to reconcile purely
from `Events` — which the docs encourage, and which
[50](50-resilient-applications-guide.md)'s application-owned-store pattern is built on — hits an
inconsistency that has nothing to do with its own design. "Mark it synced when the matching event
arrives" is exactly the shape 50 recommends, and on the online path no matching event arrives.

The second reads stronger. The whole point of the correlation id is that the application chooses
the key and Hyperwyc echoes it back; echoing it on one path and not the other makes the caller
branch on the thing the library exists to hide.

## Shape of the fix, if it is one

Small: read `HyperwycRequestOptions.CorrelationId` off the request in `HandleOnlineWriteAsync`
and pass it to the event. `OnUpdated` stays as it is — a cache refresh genuinely concerns no
write, which is what `HyperwycEvent.CorrelationId`'s own documentation says.

Open sub-question: should an online delivery *generate* a correlation id when the caller did not
supply one, as the queued path does? Probably not — there is nothing to hand it back on, since
the response is the origin's and Hyperwyc must not stamp it
([responses.md](../docs/responses.md)). Echo it when set, leave it `null` otherwise.

## Acceptance Criteria

- [ ] Either `OnDelivered` on the online path carries a caller-set correlation id, or the
      asymmetry is recorded as deliberate somewhere a reader will find it.
- [ ] `docs/events.md` says whatever turns out to be true. It currently says nothing, the
      overclaim having been removed by [58](Done/58-docs-code-reconciliation.md).
- [ ] `HyperwycEvent.CorrelationId`'s XML matches — it currently says `null` "for events that do
      not concern a queued write, such as `OnUpdated`", which does not describe this case.

## Notes

- Found from a documentation claim rather than from the code. "Every event carries a
  `CorrelationId`" was false, and the interesting part was *why* — one of the two ways it is
  false is expected and documented, and the other is this.
