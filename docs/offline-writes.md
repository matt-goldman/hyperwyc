# Offline writes

What happens to a write made with no network, when it is replayed, and how to find out what the server eventually said.

## When Hyperwyc delivers

Queued writes are flushed on exactly two triggers:

| Trigger | When |
|---|---|
| Application start | `FlushOnStartup` (default `true`), if the device is online |
| Connectivity restored | While the app is running, debounced by 2 seconds |

Plus an explicit call, for a user-facing "sync now" control:

```csharp
await hyperwyc.FlushAsync();   // IHyperwyc, resolved from DI
```

## You don't need to hook app lifecycle events

**Shutting down or backgrounding the app is deliberately not a sync trigger**, and you should
not add one. Writes are only ever queued because connectivity was poor — and closing the app
doesn't improve connectivity, so a flush at that moment would fail for the same reason the work
was queued in the first place.

Anything still queued is replayed at next launch. Nothing is lost, so there is nothing to
rescue on the way out.

This matters most on mobile, where it wouldn't work anyway: Android and iOS terminate suspended
processes without running disposal, finalizers, or any cleanup you might have registered.
Durability comes from the outbox being persistent, not from tidying up at exit.

## When a write fails

One distinction decides everything: **did the server answer?**

| Failure | What happens |
|---|---|
| The server answered, with anything other than success | Dead-lettered, and the status reported on [`Events`](events.md). The request reached the API, which was the job |
| No response at all — the connection failed | Left queued. The flush stops and nothing is held against the remaining writes; the network being down says nothing about them |

**Any answer is a final outcome, including a `500`, a `429` or a `503`.** That looks harsh until
you ask what a retry here would actually be. Hyperwyc has exactly three triggers — application
start, connectivity restored, and an explicit `FlushAsync()` — and none of them correlates with a
server recovering. Requeuing a `503` schedules a retry on an unrelated event, and for a device
that never goes offline again it schedules one that never arrives. A write kept on that promise
is kept forever.

Your own pipeline has already had the better attempt. A replay traverses it (see
[Pipeline placement](pipeline.md)), so a resilience handler retries on a schedule that tracks the
actual failure, with backoff and `Retry-After`, before Hyperwyc ever sees the result. Hyperwyc
adding a second, worse retry on top would be duplicating a job that has an owner — see
[ADR 0001](decisions/0001-idempotency-is-not-hyperwycs-remit.md).

Dead-lettered is not discarded. The status, reason phrase and response body are recorded against
the envelope and published, so the application can decide what to do with information Hyperwyc
does not have.

## Duplicate writes

Any retry can deliver the same request twice — if a response is lost after the server has
already committed, the retry looks identical to a first attempt. This is true of a Polly retry
handler, a user double-tapping a button, or a proxy replaying a request. Hyperwyc's retry carries
the same risk and no more.

**Hyperwyc takes no position on it.** It sends no headers of its own on the wire and asks nothing
of your API — the [two it adds](responses.md#headers) go on responses it synthesises, which your
server never sees. Duplicate suppression is between your application and your backend. If it
matters to you, approaches people use include:

- **Client-generated domain identity** — the record carries an id chosen by the client, so a
  repeated write updates rather than duplicates. Idempotent by construction, and nothing in the
  transport needs to know.
- **The [`Idempotency-Key`](https://datatracker.ietf.org/doc/draft-ietf-httpapi-idempotency-key-header/)
  header**, set at the call site, if your backend implements it. Hyperwyc persists request headers
  and replays them unchanged, so a key you set once stays stable across every retry:

  ```csharp
  request.Headers.Add("Idempotency-Key", sale.Id.ToString());
  ```

- **A correlation or transaction id you already emit** — common in event-driven systems, and
  increasingly generated in the UI so analytics can be tied to backend telemetry.

These are things people do, not a recommendation from Hyperwyc. Which one fits, or whether the
concern applies at all, depends on your API.

## Writes are queued on transport failure too, not just when you are offline

Hyperwyc does not only queue when `IConnectivityService` says offline. If a write is attempted
because the device reports connected, and **the transport cannot establish a connection at all**,
that write is queued and answered with the same `202` as if it had been made offline.

This matters because every connectivity implementation is wrong sometimes — a captive portal, a
VPN interface that looks like a network, a signal that drops between the check and the send, or
`AlwaysOnlineConnectivityService` on a device that is not. Without this, being wrong would cost
the write. With it, being wrong costs an attempt.

> **Only when nothing was sent.** Hyperwyc queues on the transport errors that mean no connection
> was ever established — DNS failure, connection refused, TLS handshake failure, proxy tunnel
> failure. If a connection *was* made and the failure came later, the request is not queued and
> the exception reaches you: the server may have processed it, and quietly replaying it would
> risk a duplicate on a guess. Those are also failures no connectivity change would fix.

The same rule the rest of the library follows: connectivity is a hint about which path to try
first, and where the transport is consulted, it is what actually knows. Note the limit — a
connectivity service that wrongly reports *offline* is never contradicted, because no request is
made to contradict it. That write is queued rather than sent, and goes out on the next
connectivity change.

## Interrupted deliveries

If a flush is cut short — the app is backgrounded mid-replay, or the process is killed — the
envelopes it hadn't delivered stay queued and go out on the next trigger. They are not marked
as failed, and they are not dead-lettered. Only a request the server actually rejected, after
the server refuses, ends up in the dead-letter queue.

---
