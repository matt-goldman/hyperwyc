# Offline writes

What happens to a write made with no network, when it is replayed, and how to find out what the server eventually said.

## When Hyperwyc delivers

Queued writes are automatically flushed by two triggers:

| Trigger               | When                                                       |
| --------------------- | ---------------------------------------------------------- |
| Application start     | `FlushOnStartup` (default `true`), if the device is online |
| Connectivity restored | While the app is running, debounced by 2 seconds           |

[comment: There is no debounce. It was removed in the ADR 0004 audit - it is named in 0004's own list of what came out - and OutboxProcessor says so explicitly: "No debounce. FlushAsync try-acquires the flush gate and returns [immediately if one is already running]". What actually stops a burst of connectivity events piling up is the single-flush gate, which is the more useful thing for a reader to know anyway, so this is a replacement rather than a deletion.]

You can also call this explicitly, for example if certain lifecycle events in your app warrant it, or for a user-facing "sync now" control (something you should do if you expose queued writes to your users):

```csharp
await hyperwyc.FlushAsync();   // IHyperwyc, resolved from DI
```

## You don't need to hook app lifecycle events

**Shutting down or backgrounding the app is deliberately not a sync trigger**, and you should not add one. Writes are only ever queued because connectivity was poor, and closing the app doesn't improve connectivity, so a flush at that moment would fail for the same reason the work was queued in the first place.

Anything still queued is replayed at next launch. Nothing is lost, so there is nothing to rescue on the way out.

This matters most on mobile, where it wouldn't work anyway: Android and iOS terminate suspended processes without running disposal, finalizers, or any cleanup you might have registered. Durability comes from the outbox being persistent, not from tidying up at exit.

## When a write fails

One distinction decides everything: **did the server answer?**

| Failure                                               | What happens                                                                                                                  |
| ----------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------- |
| The server answered, with anything other than success | Dead-lettered, and the status reported on [`Events`](events.md). The request reached the API, which was the job               |
| No response at all — the connection failed            | Left queued. The flush stops and nothing is held against the remaining writes; the network being down says nothing about them |

[comment: The table is right, and what is missing from it matters: a transport failure during a flush publishes no event at all. The outcome is recorded on the envelope, but nothing reaches the event stream - so an application watching Events sees a write queued and then silence, until it eventually delivers or the app is restarted. That is deliberate (it is one of the reasons backlog item 23, the diagnostics view, exists) but a reader building on Events needs to know it, and this table is the only place they would think to look.]

TODO: we use the term "dead lettered" throughout the docs, should we explain what this means somewhere? A reader not familiar with the term, or without the context that it applies purely to the write queue and only after the server has already received it, may be confused and concerned. Dead lettered really means the same thing as succeeded from Hyperwyc's perspective - the HTTP request _did_ succeed, well not semantically strictly true if not a 2xx response, but Hyperwyc succeeded in delivering it.

[comment: Agreed, and your framing is the right one to use: from Hyperwyc's side the delivery succeeded, and what failed is downstream of the job it took on. The word is borrowed from message queues, where it does mean something closer to "we gave up", so a reader who knows the term is more likely to be alarmed than one who does not.

Two things fix it: a one-sentence definition at first use on this page, and a glossary. I have filed both. The glossary earns its place well beyond this one term - outbox, envelope, replay, flush, synthetic response, correlation id, dead-letter and even "stale" are all used as though already defined, and ADR 0005 did the thinking for it already; a glossary is just its consumer-facing face.]

**Any answer is a final outcome, including a `500`, a `429` or a `503`.** Remember that Hyperwyc's job is to make sure your request reaches your back end, and a response, any response, means it has succeeded. Hyperwyc is not responsible for retrying failed requests; it has exactly three triggers — application start, connectivity restored, and an explicit `FlushAsync()` — and none of them correlates with a change to the condition under which the request failed. Requeuing a `503` schedules a retry on an unrelated event, and for a device that never goes offline again it schedules one that never arrives. A write kept on that promise is kept forever.

Other approaches already handle these scenarios, and with a library lke [Polly](https://github.com/App-vNext/Polly), your pipeline has already had the better attempt. A replay traverses it (see [Pipeline placement](pipeline.md)), so a resilience handler retries on a schedule that tracks the
actual failure, with backoff and `Retry-After`, before Hyperwyc ever sees the result. Hyperwyc adding a second, worse retry on top would be duplicating a job that has an owner (see [ADR 0001](decisions/0001-idempotency-is-not-hyperwycs-remit.md)).

[comment: "exactly three triggers" above versus "two triggers" at the top of the page. Both are true - the table counts automatic triggers and this counts the manual one too - but they are 25 lines apart and read as a contradiction. Making the table's lead-in say "automatically" would settle it. ("lke" below.)]

**Note:** If you are using something like Polly, especially in a UI app, an implementation of Hyperwyc's `IConnectivityService` will drastically improve UX as it will short-circuit retries when offline. TODO: validate this; does Polly already let you branch on failure reason? I think it does as I've built 401 refresh handlers before.

[comment: Partly yes, but the claim as written credits the wrong mechanism.

Polly can branch on failure reason - HttpRetryStrategyOptions.ShouldHandle takes a predicate over the outcome, so you can match on exception type, on status code, or on HttpRequestException.HttpRequestError specifically. Your 401-refresh recollection is right and that is how it is done.

But that is not what an IConnectivityService buys you here. What actually happens is that when Hyperwyc believes it is offline it short-circuits the pipeline and returns the synthetic response directly, so a resilience handler registered *after* HyperwycHandler is never invoked at all - no predicate, no first attempt, no backoff. pipeline.md documents this as a design property. So the UX improvement is real and it is bigger than the sentence claims: there is no retry to short-circuit, because there is no request.

Two conditions worth stating alongside it. It only holds for a handler registered after Hyperwyc's - registered before, the resilience handler sees the synthetic 202 or 200 and, depending on how you configured ShouldHandle, may retry a response Hyperwyc invented. And it only holds when the connectivity service actually reports offline, which puts you back on the "which way does it err" question the rest of the page is about: an implementation that errs toward connected buys you nothing here, by design.]

Dead-lettered is not discarded. The status, reason phrase and response body are recorded against the envelope and published, so the application can decide what to do with information Hyperwyc does not have. It just means Hyperwyc won't ever try to send it again.

## Duplicate writes

Any retry can deliver the same request twice. If a response is lost after the server has already committed, the retry looks identical to a first attempt. This is true of a Polly retry handler, a user double-tapping a button, or a proxy replaying a request. Hyperwyc's retry carries the same risk and no more.

**Hyperwyc takes no position on it.** It sends no headers of its own on the wire and asks nothing of your API — the [two it adds](responses.md#headers) go on *responses* it synthesises, not requests; the headers never leave your client, and your server never sees them. Duplicate suppression is between your application and your backend. If it matters to you, approaches people use include:

- **Client-generated domain identity** — the record carries an id chosen by the client, so a repeated write updates rather than duplicates (or rejects with a `409`; the update may be valid for `PUT` or `PATCH` but not `POST`, but, again, this is the business of your API, not Hyperwyc). Idempotent by construction, and nothing in the transport needs to know.
- **The [`Idempotency-Key`](https://datatracker.ietf.org/doc/draft-ietf-httpapi-idempotency-key-header/) header**, set at the call site, if your backend implements it. Hyperwyc persists request headers and replays them unchanged, so a key you set once stays stable across every retry:

  ```csharp
  request.Headers.Add("Idempotency-Key", sale.Id.ToString());
  ```

- **A correlation or transaction id you already emit** — common in event-driven systems, and increasingly generated in the UI so analytics can be tied to backend telemetry.

These are things people do, not a recommendation from Hyperwyc. Which one fits, or whether the concern applies at all, depends on your solution.

## Writes are queued on transport failure too, not just when you are offline

Hyperwyc does not only queue when `IConnectivityService` says offline. If a write is attempted because the device reports connected, and **the transport cannot establish a connection at all**, that write is queued and answered with the same `202` as if it had been made offline.

This matters because every connectivity implementation is wrong sometimes — a captive portal, a VPN interface that looks like a network, a signal that drops between the check and the send, or `AlwaysOnlineConnectivityService` on a device that is not. Without this, being wrong would cost the write. With it, being wrong costs an attempt.

[comment: This section is the answer to "what happens when my connectivity implementation is wrong", which is the question the whole connectivity page raises and never quite closes. It is also near the bottom of a page a reader may not reach, and connectivity.md's summary table describes exactly this behaviour in its first row without saying where it is documented. At minimum, link back to here from that table.]

> **Only when nothing was sent.** Hyperwyc queues on the transport errors that mean no connection
> was ever established — DNS failure, connection refused, TLS handshake failure, proxy tunnel
> failure. If a connection *was* made and the failure came later, the request is not queued and
> the exception reaches you: the server may have processed it, and quietly replaying it would
> risk a duplicate on a guess. Those are also failures no connectivity change would fix.

The same rule the rest of the library follows: connectivity is a *hint* about which path to try first, and where the transport is consulted, it is what actually knows. Note the limit: a connectivity service that wrongly reports *offline* is never contradicted, because no request is made to contradict it. That write is queued rather than sent, and only goes out on the next connectivity change, or when the `IHyperwyc.FlushAsync()` method is called.

## Interrupted deliveries

If a flush is cut short, e.g. the app is backgrounded mid-replay, or the process is killed, the envelopes it hadn't delivered stay queued and go out on the next trigger. They are not marked as failed, and they are not dead-lettered. Only a request the server actually rejected, after the server refuses, ends up in the dead-letter queue.

[comment: "Only a request the server actually rejected, after the server refuses" - the clause repeats itself.]
