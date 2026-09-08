# Events

Hyperwyc reports what it did. Subscribe to find out that a write was queued, delivered or refused, and what the server said when it was.

Subscribe to `IObservable<HyperwycEvent>` to observe requests moving through the sync lifecycle:

```csharp
hyperwyc.Events.Subscribe(e => Console.WriteLine($"{e.Type}: {e.Url}"));
```

| Event               | Meaning                                                                                     | Carries an outcome              |
| ------------------- | ------------------------------------------------------------------------------------------- | ------------------------------- |
| `OnQueued`          | Request persisted to local queue (offline)                                                  | No — nothing has been attempted |
| `OnDelivered`       | Request successfully delivered                                                              | Yes                             |
| `OnFailed`          | Request dead-lettered — the server refused it                                               | Yes                             |
| `OnUpdated`         | Cached response refreshed                                                                   | No                              |
| `OnStoreUnreadable` | The local store could not be read; caching and queueing are off for the rest of the session | No                              |

[comment: Two things this table does not say that a reader building on it needs.

OnQueued's "(offline)" is now incomplete. It also fires when the device believed it was online and the transport could not connect - the ADR 0007 path - which on a mobile device with a weak connectivity implementation may well be the common case.

And nothing at all is published when a delivery attempt fails at the transport during a flush. Not OnFailed, not anything: the outcome is recorded on the envelope and the flush stops. So a write that cannot be delivered goes quiet until it eventually is. That is the gap backlog item 23 exists to fill, and it should be stated here, because this page opens by promising that Hyperwyc reports what it did.]

These are Hyperwyc's own events, not your app's lifecycle. See below for how the two relate.

## Knowing which request an event is about

When a write is deferred, the caller has already been given a `202` and moved on. For the eventual outcome to be useful, the event has to say *which* write it concerns; three `POST` requests queued to `/sales` are indistinguishable by URL and method.

TODO: correlation ID should probably be mentioned in delivery. Or given more attention prior to just events, currently it has a brief mention in offline-writes but there does not appear to be a canonical point in the docs that owns the concept.

[comment: Agreed that it needs an owner, and I think this page is it - the correlation id exists so that an outcome arriving later can be matched to a write made earlier, which is precisely what this page is about. The fix is small: keep the full explanation here, and have responses.md's header table and offline-writes.md link to it instead of restating it.

I would not add it to delivery.md. That page is about where a response comes from; correlation is about what happens afterwards, and putting it there would widen a page that already covers two topics.]

Every event carries a `CorrelationId`. **If you set one, Hyperwyc uses it**, which means you can correlate on an id you already have and keep no mapping table:

[comment: "Every event carries a CorrelationId" is not true. It is null on OnUpdated, which is expected and documented on the type - a cache refresh concerns no write. Less obviously, it is also null on the OnDelivered published from the *online* write path, which is constructed with four arguments and no correlation id at all.

So a caller who sets HyperwycRequestOptions.CorrelationId and whose write then goes out online receives an OnDelivered they cannot correlate. That may well be fine - they also got the real response synchronously, so they have their answer by another route - but it is not what this sentence promises, and an app that drives its UI off the event stream uniformly would have a hole in exactly the happy path. Worth deciding whether that asymmetry is deliberate; if it is, this sentence should say "every event about a queued write".]

```csharp
var request = new HttpRequestMessage(HttpMethod.Post, "/sales")
{
    Content = JsonContent.Create(sale)
};
request.Options.Set(HyperwycRequestOptions.CorrelationId, sale.Id.ToString());

await client.SendAsync(request);
```

If you don't, Hyperwyc generates one and returns it on the `202` as [`X-Hyperwyc-Correlation-Id`](responses.md#headers), so you can record the association at the moment you queue:

```csharp
var response = await client.PostAsJsonAsync("/sales", sale);
sale.CorrelationId = response.Headers.GetValues("X-Hyperwyc-Correlation-Id").Single();
```

The header is present either way. Hyperwyc neither requires the value to be unique nor deduplicates on it; it is your key, carrying your meaning. It is **not** an idempotency key; see [ADR 0001](decisions/0001-idempotency-is-not-hyperwycs-remit.md).

Events also carry `RequestId` (Hyperwyc's own unique envelope id, which a diagnostics view would use) and `RequestBody`, so you can deserialise your own payload back out if you'd rather not keep a copy.

## Reading the outcome

`DeliveryOutcome` is what the server or the network actually resulted in:

TODO: this snipped requires Rx doesn't it?

[comment: Yes. .Where() over IObservable<T> is System.Reactive.Linq. HyperwycEventStream is a hand-rolled IObservable with no operators on it, so this snippet does not compile against Hyperwyc alone, and nothing on the page says so.

My first instinct was to replace it with the plain IObserver form. That is only half right: Rx belongs in most UI applications, and a MAUI or WPF consumer reaching for it is doing the right thing rather than taking on an unnecessary dependency. Dropping the version most people should ideally use, to keep a snippet dependency-free, trades the better advice for the more portable one.

So it is a placement question rather than a choice, and the two forms have different readers. The plain IObserver form is reference - it compiles against Hyperwyc alone and shows the event shape without asking anything of the reader's project, which is what this page is for. The Rx query is a pattern: it is how you actually wire outcomes into a view model, and it belongs alongside correlating a 202 to a local record and marking unsynced then synced. That is the patterns page in the restructure.

Which is a small argument for the restructure rather than against it - the reason this snippet is wrong is that the page is trying to be reference and pattern at once, and the pattern half carries a dependency the reference half must not.

One clause worth adding somewhere while this is fixed: the no-Rx decision is about Hyperwyc's dependency graph, not yours. connectivity.md states it twice without that distinction, so a reader can come away thinking Rx is discouraged for them, which is not the position. Filed as 59.]

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

[comment: This branch is unreachable. OnFailed is published only from DeadLetterAsync, so Outcome.Kind is always Rejected inside this subscription - a transport failure publishes no event at all, as noted on the table above. The comment describes a callback that never fires.

Once the Kind row is corrected, this branch should go, and the honest replacement is a sentence rather than code: silence is what a transport failure looks like from the event stream.]

| Member                                     | What it tells you                                                                                                                       |
| ------------------------------------------ | --------------------------------------------------------------------------------------------------------------------------------------- |
| `Kind`                                     | `Succeeded`, `Rejected` (a 4xx — it will never work), `TransientFailure` (a 5xx/408/429), `TransportFailure` (never reached the server) |
| `StatusCode`, `ReasonPhrase`, `Headers`    | As returned, or `null`/empty for a transport failure                                                                                    |
| `Body`, `GetBodyAsText()`, `BodyTruncated` | The response body, up to `MaxOutcomeBodyBytes` (16 KB by default), clipped rather than dropped if longer                                |
| `Error`                                    | The transport failure message. A string rather than an exception, because this record is persisted                                      |

[comment: The Kind row is stale in both halves, and this is the one stale line in the docs that will break a consumer's code rather than merely mislead them, because it names an enum member that does not exist.

TransientFailure was removed when any server response became final. DeliveryOutcomeKind now has three members: Succeeded, Rejected, TransportFailure.

The row below it has the same problem: Headers is not a member of DeliveryOutcome either. It went in the same ADR 0004 audit, which names it explicitly in its list of what came out. So StatusCode and ReasonPhrase are right and Headers should go.

And Rejected is no longer "a 4xx - it will never work". It is any non-success status, including 5xx, 408 and 429, which is the change offline-writes.md documents correctly. "It will never work" is also the wrong reading now: it means Hyperwyc will not attempt it again, which is a statement about Hyperwyc rather than about the request. The application may well retry it, and the whole point of persisting the outcome is to let it decide.]

`OnDelivered` carries an outcome too. A replayed `POST` may answer with the created resource (server-assigned ids, normalised values) which the caller never saw, so this is how you reconcile your local record with what was actually stored.

> **Two things this does not do.** Failure detail is persisted on the envelope so a dead-lettered
> write can still explain itself after a restart, but **success detail is not** — a delivered
> envelope leaves the outbox, so if your app was killed mid-flush that response is gone. Re-read
> the resource if you need certainty. And Hyperwyc has no opinion on what you do with any of
> this: prompt, auto-reduce, back-order, escalate, discard. It hands you what the server said and
> stops there.

<details>
<summary>A framing note, if it's useful</summary>

Surfacing outcomes this way nudges you toward describing the action rather than its result - "order **submitted**", not "order **successful**" — with the outcome arriving separately and later. Most teams already think this way about their backend without having carried it into the
client. It is genuinely optional: Hyperwyc does not require anyone to model their UI a particular way.

TODO: I mentioned this elsewhere, think it needs more attention and possible relocation. I mentioned a potential "opinions" doc, I'm now thinking "patterns" might be the right approach. A lot of this documentation mixes "here's what you need to do" with "here's how it works under the hood". Both are useful but there should be a clear separation between what consumers need to read for it to be useful vs what people deep-diving need.

[comment: Agreed, and there is a longer note at delivery.md's version of this TODO. Short version: I think it is two pages rather than one, and the second of them is backlog item 50, which is already specified in some detail.

On this passage specifically - it is a principles one rather than a patterns one. It is a stance about how to name things in a UI, not a thing to build, and it currently sits behind a collapsed details element on a reference page, which is about the least discoverable place a stance can be. It is also, as it happens, the clearest statement anywhere of the idea that ties the 202, the correlation id, the event stream and the "no data" read into one design instead of four separate decisions. That is worth more than a footnote.]

</details>
