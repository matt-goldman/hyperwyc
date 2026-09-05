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

Not every failure means the same thing, so Hyperwyc doesn't treat them the same way:

| Failure | What happens |
|---|---|
| `4xx` — the server rejected it | Dead-lettered immediately. Sending the identical request again cannot change the answer |
| `5xx`, `408`, `429` — the server is struggling | Left queued, and attempted again on the next flush. There is no attempt budget: Hyperwyc keeps the write until the server takes it or you discard it |
| Can't reach the network at all | The flush stops and nothing is held against the queued writes — the network being down says nothing about them |

Hyperwyc's job is getting writes out once the network allows it, so that is the failure it
retries. Anything your own handlers already deal with — refreshing a token, tripping a circuit
breaker, retrying a flaky endpoint — has run before Hyperwyc sees the result, and it doesn't
second-guess them.

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

## Interrupted deliveries

If a flush is cut short — the app is backgrounded mid-replay, or the process is killed — the
envelopes it hadn't delivered stay queued and go out on the next trigger. They are not marked
as failed, and they are not dead-lettered. Only a request the server actually rejected, after
the server refuses, ends up in the dead-letter queue.

---
