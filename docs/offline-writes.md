# Offline writes

What happens to a write made with no network, when it is replayed, and how to find out what the server eventually said.

## When Hyperwyc delivers

| Trigger               | When                                                                |
| --------------------- | ------------------------------------------------------------------- |
| Connectivity restored | Automatic, while the app is running                                 |
| Application start     | If `FlushOnStartup` is `true`. Defaults to `false`, and needs a host |
| `FlushAsync()`        | Whenever you call it                                                |

```csharp
await hyperwyc.FlushAsync();   // IHyperwyc, resolved from DI
```

Call it for a user-facing "sync now" control — something you should offer if you expose queued writes to your users — and for any lifecycle event in your app that warrants one.

### Which way to flush at startup

Both are supported; the difference is whether you need to hear what the server said.

**Set `FlushOnStartup = true`** if your event subscriber is composed before the host starts, or if you do not need the outcomes at all and simply want the queue drained. It is one line of configuration and nothing else to remember.

**Leave it off and call `FlushAsync()` yourself** if the thing that listens is built later — a page, a view model, a lazily-resolved service. That is the common shape in a UI app, and it is why the default is `false`: the startup flush completes inside host startup, so a subscriber that attaches afterwards misses whatever it delivered, and Hyperwyc keeps nothing about a delivered write for it to catch up from.

```csharp
hyperwyc.Events.Subscribe(new MyObserver());
await hyperwyc.FlushAsync();
```

Either way, note that `FlushOnStartup` is implemented as an `IHostedService`, so in an application built on a bare `ServiceCollection` it does nothing at all.

## You don't need to hook app lifecycle events

**Shutting down or backgrounding the app is deliberately not a sync trigger**, and you should not add one. Writes are only ever queued because connectivity was poor, and closing the app doesn't improve connectivity, so a flush at that moment would fail for the same reason the work was queued in the first place.

Anything still queued is replayed at next launch. Nothing is lost, so there is nothing to rescue on the way out.

This matters most on mobile, where it wouldn't work anyway: Android and iOS terminate suspended processes without running disposal, finalizers, or any cleanup you might have registered. Durability comes from the outbox being persistent, not from tidying up at exit.

## What happens when a write goes out

One distinction decides everything: **did the server answer?**

Not *what* it answered. [Hyperwyc succeeds or fails at delivery](design.md#delivery-is-what-hyperwyc-succeeds-or-fails-at), and HTTP's own idea of success is a different axis that happens to use the same word.

| | What happens |
| ----------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------- |
| The server answered — with anything at all | The delivery is complete. `OnDelivered` carries the status, reason phrase and body, and the envelope is discarded. The request reached the API, which was the job |
| No response at all — the connection failed            | Left queued. The flush stops and nothing is held against the remaining writes; the network being down says nothing about them |

```mermaid
stateDiagram-v2
    state "queued in the outbox" as queued
    state "delivered, and discarded" as done
    [*] --> queued : could not be sent — OnQueued
    queued --> queued : transport failed — no event
    queued --> done : server answered, whatever it said — OnDelivered
    done --> [*]
```

Every transition raises an event except one. A delivery attempt that fails at the transport records its outcome on the envelope and stops the flush, and publishes nothing at all — so from the event stream, a write that cannot be delivered simply goes quiet until it can be.

**One right-hand state, not two.** Refused and accepted writes are treated the same way, because they are the same thing from Hyperwyc's side: the envelope goes, request body and headers with it, and what the server said reaches you on the event. Nothing about a delivered write is kept — see [ADR 0010](decisions/0010-delivery-ends-hyperwycs-interest.md), which is honest about what that costs you.

> **So subscribe before you flush.** The event is the *only* report of a delivery, and absence from the outbox does not distinguish accepted from rejected. If what the server said is something you need — a rejection's body is often the only account of why — read it when the event arrives and file it under your own correlation id, in your own store, which is [the record you were keeping anyway](design.md).

**Any answer is a final outcome, including a `500`, a `429` or a `503`.** Remember that Hyperwyc's job is to make sure your request reaches your back end, and a response, any response, means it has succeeded. Hyperwyc is not responsible for retrying failed requests; it has exactly three triggers — connectivity restored, an explicit `FlushAsync()`, and application start if you opt into it — and none of them correlates with a change to the condition under which the request failed. Requeuing a `503` schedules a retry on an unrelated event, and for a device that never goes offline again it schedules one that never arrives. A write kept on that promise is kept forever.

Other approaches handle these failures, and can be wired into your pipeline with a resilience handler such as [Polly](https://github.com/App-vNext/Polly), which retries on a schedule that tracks the actual failure before Hyperwyc ever sees the result. [Why Hyperwyc does not retry](design.md#hyperwyc-does-not-retry) has the rest.

**Note:** If you use a resilience handler such as Polly, register it *after* `AddHyperwycHandler()` and supply an `IConnectivityService`. When Hyperwyc believes it is offline it answers from the handler and the rest of the pipeline is never invoked, so there is no doomed first attempt and no backoff to sit through — see [Pipeline placement](pipeline.md). In a UI app that is the difference between an instant cached read and a retry schedule the user waits out.

## Duplicate writes

Any retry can deliver the same request twice. If a response is lost after the server has already committed, the retry looks identical to a first attempt. This is true of a Polly retry handler, a user double-tapping a button, or a proxy replaying a request. Hyperwyc's retry carries the same risk and no more.

**Hyperwyc takes no position on it**, and you can see why [in detail here](design.md#hyperwyc-takes-no-position-on-duplicates). It sends no headers of its own on the wire — the [two it adds](responses.md#headers) go on *responses* it synthesises, so your server never sees them. If duplicate suppression matters to you, approaches people use include:

- **Client-generated domain identity** — the record carries an id chosen by the client, so a repeated write updates rather than duplicates (or rejects with a `409`; the update may be valid for `PUT` or `PATCH` but not `POST`, but, again, this is the business of your API, not Hyperwyc). Idempotent by construction, and nothing in the transport needs to know.
- **The [`Idempotency-Key`](https://datatracker.ietf.org/doc/draft-ietf-httpapi-idempotency-key-header/) header**, set at the call site, if your backend implements it. Hyperwyc persists request headers and replays them unchanged, so a key you set once stays stable across every retry:

  ```csharp
  request.Headers.Add("Idempotency-Key", sale.Id.ToString());
  ```

- **A correlation or transaction id you already emit** — common in event-driven systems, and increasingly generated in the UI so analytics can be tied to backend telemetry.

These are things people do, not a recommendation from Hyperwyc. Which one fits, or whether the concern applies at all, depends on your solution.

## Writes are queued on transport failure too, not just when you are offline

Hyperwyc does not only queue when `IConnectivityService` says offline. If a write is attempted because the device reports connected, and **the transport cannot establish a connection at all**, that write is queued and answered with the same `202` as if it had been made offline.

This matters because every connectivity implementation is wrong sometimes — a captive portal, a signal that drops between the check and the send, or `AlwaysOnlineConnectivityService` on a device that is not. Without this, being wrong would cost the write. With it, being wrong costs an attempt.

> **Only when nothing was sent.** Hyperwyc queues on the transport errors that mean no connection
> was ever established — DNS failure, connection refused, TLS handshake failure, proxy tunnel
> failure. If a connection *was* made and the failure came later, the request is not queued and
> the exception reaches you: the server may have processed it, and quietly replaying it would
> risk a duplicate on a guess. Those are also failures no connectivity change would fix.

The same rule the rest of the library follows: connectivity is a *hint* about which path to try first, and where the transport is consulted, it is what actually knows. Note the limit: a connectivity service that wrongly reports *offline* is never contradicted, because no request is made to contradict it. That write is queued rather than sent, and only goes out on the next connectivity change, or when the `IHyperwyc.FlushAsync()` method is called.

## Interrupted deliveries

If a flush is cut short, e.g. the app is backgrounded mid-replay, or the process is killed, the envelopes it hadn't delivered stay queued and go out on the next trigger. Nothing is held against them: only a delivery removes an envelope, and an interrupted flush delivered nothing.

