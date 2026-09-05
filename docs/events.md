# Events

Hyperwyc reports what it did. Subscribe to find out that a write was queued, delivered or refused — and what the server said when it was.

Subscribe to `IObservable<HyperwycEvent>` to observe requests moving through the sync lifecycle:

```csharp
hyperwyc.Events.Subscribe(e => Console.WriteLine($"{e.Type}: {e.Url}"));
```

| Event | Meaning |
|-------|---------|
| Event | Meaning | Carries an outcome |
|-------|---------|---|
| `OnQueued` | Request persisted to local queue (offline) | No — nothing has been attempted |
| `OnDelivered` | Request successfully delivered | Yes |
| `OnFailed` | Request dead-lettered — the server refused it | Yes |
| `OnUpdated` | Cached response refreshed | No |
| `OnStoreUnreadable` | The local store could not be read; caching and queueing are off for the rest of the session | No |

These are Hyperwyc's own events, not your app's lifecycle — see below for how the two relate.

## Knowing which request an event is about

When a write is deferred, the caller has already been given a `202` and moved on. For the
eventual outcome to be useful, the event has to say *which* write it concerns — three sales
queued to `/sales` are indistinguishable by URL and method.

Every event carries a `CorrelationId`. **If you set one, Hyperwyc uses it**, which means you can
correlate on an id you already have and keep no mapping table:

```csharp
var request = new HttpRequestMessage(HttpMethod.Post, "/sales")
{
    Content = JsonContent.Create(sale)
};
request.Options.Set(HyperwycRequestOptions.CorrelationId, sale.Id.ToString());

await client.SendAsync(request);
```

If you don't, Hyperwyc generates one and returns it on the `202` as
`X-Hyperwyc-Correlation-Id`, so you can record the association at the moment you queue:

```csharp
var response = await client.PostAsJsonAsync("/sales", sale);
sale.CorrelationId = response.Headers.GetValues("X-Hyperwyc-Correlation-Id").Single();
```

The header is present either way. Hyperwyc neither requires the value to be unique nor
deduplicates on it — it is your key, carrying your meaning. It is **not** an idempotency key;
see [ADR 0001](decisions/0001-idempotency-is-not-hyperwycs-remit.md).

Events also carry `RequestId` (Hyperwyc's own unique envelope id, which a diagnostics view would
use) and `RequestBody`, so you can deserialise your own payload back out if you'd rather not
keep a copy.

## Reading the outcome

`DeliveryOutcome` is what the server — or the network — actually said:

```csharp
hyperwyc.Events
    .Where(e => e.Type == HyperwycEventType.OnFailed)
    .Subscribe(e =>
    {
        var outcome = e.Outcome!;

        if (outcome.Kind == DeliveryOutcomeKind.Rejected)
        {
            // The server refused it. outcome.StatusCode is 409, and the reason is in the body.
            var detail = outcome.GetBodyAsText();
            ShowRejection(e.CorrelationId!, outcome.StatusCode, detail);
        }
        else
        {
            // Never reached the server. Still queued; nothing to do but wait.
            ShowPending(e.CorrelationId!);
        }
    });
```

| Member | What it tells you |
|---|---|
| `Kind` | `Succeeded`, `Rejected` (a 4xx — it will never work), `TransientFailure` (a 5xx/408/429), `TransportFailure` (never reached the server) |
| `StatusCode`, `ReasonPhrase`, `Headers` | As returned, or `null`/empty for a transport failure |
| `Body`, `GetBodyAsText()`, `BodyTruncated` | The response body, up to `MaxOutcomeBodyBytes` (16 KB by default), clipped rather than dropped if longer |
| `Error` | The transport failure message. A string rather than an exception, because this record is persisted |

`OnDelivered` carries an outcome too. A replayed `POST` may answer with the created resource —
server-assigned ids, normalised values — which the caller never saw, so this is how you reconcile
your local record with what was actually stored.

> **Two things this does not do.** Failure detail is persisted on the envelope so a dead-lettered
> write can still explain itself after a restart, but **success detail is not** — a delivered
> envelope leaves the outbox, so if your app was killed mid-flush that response is gone. Re-read
> the resource if you need certainty. And Hyperwyc has no opinion on what you do with any of
> this: prompt, auto-reduce, back-order, escalate, discard. It hands you what the server said and
> stops there.

<details>
<summary>A framing note, if it's useful</summary>

Surfacing outcomes this way nudges you toward describing the action rather than its result —
"order **submitted**", not "order **successful**" — with the outcome arriving separately and
later. Most teams already think this way about their backend without having carried it into the
client. It is genuinely optional: Hyperwyc does not require anyone to model their UI a particular
way.

</details>

---
