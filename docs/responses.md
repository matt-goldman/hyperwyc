# Synthetic responses

What a caller sees when Hyperwyc answers instead of the server: the status codes, the two headers, and the body.

Hyperwyc answers a request itself in exactly two cases:

| Case                                                | Status         | `X-Hyperwyc-Status` |
| --------------------------------------------------- | -------------- | ------------------- |
| A write was queued for later delivery               | `202 Accepted` | `Queued`            |
| A read requested while offline had nothing to serve | `200 OK`       | `Offline`           |

Either can arise two ways: the connectivity service reported offline, or the request was attempted and [the transport could not answer](offline-writes.md). Both produce the identical response — the caller cannot tell which happened, and does not need to. So a connectivity implementation that wrongly reports *online* costs the time it takes for a failed request and nothing else. One that wrongly reports *offline* is not corrected, because the transport is never asked; that costs freshness and delays a write. Neither loses data. See [Connectivity](connectivity.md) for which direction matters.

Everything else your caller sees — including a cache hit — is a real server response, returned unchanged.

[comment: Fourth full statement of the false-positive/false-negative asymmetry in the docs. Here it is the least necessary of them: this page's job is "what does a caller see", and the answer is "identical either way, and you cannot tell which happened". That sentence carries the whole point; the rest can be a link to wherever the asymmetry ends up living once.]

## Status codes

**`202 Accepted` — the write is queued.** Not `200`, because the request has been accepted for later processing and has not yet been performed against the origin server. It is also how a caller tells a queued write from a delivered one without reading a header, but only if no ordinary success from your API is a `202`. The code is fixed — there is no option to change it. TODO: should this be configurable in per-route policies?

[comment: I would answer no, and answer it on the page rather than leaving it open.

The 202 is the only in-band signal separating a queued write from a delivered one. Making it configurable lets a consumer configure that distinction away, and the failure mode is both silent and severe - a caller treats a queued write as done. That is defaults test question 3, "could a wrong value quietly produce the failure the library exists to prevent", which is a never rather than a trade-off.

The scope test points the same way. A consumer who needs a different code has one line at the call site, or one handler registered above Hyperwyc's, so question 3 of the scope test says our job is to not interfere.

And the "only if no ordinary success from your API is a 202" caveat you have already written is the real constraint, which a knob would not relieve. An API that genuinely returns 202s needs the header rather than a different code, because whatever code you configured could collide the same way - you would have moved the problem, not solved it. That is worth saying explicitly, because it is the argument a consumer will bring.

Filed as under-consideration with this reasoning so the question is answered rather than re-asked, but I think the answer belongs on this page as a deliberate absence, in the same shape as the other "we do not do this, here is why" notes.]

**`200 OK` — the read found nothing.** Offline with no cached copy, or a cached copy past its [TTL](delivery.md). Not a `404` and not a `503`: nothing was rejected and nothing is missing, Hyperwyc simply has no data for the route. Your code handles the empty result the same way it already handles a search with no matches — see [Designing your responses](delivery.md#designing-your-responses). TODO: we should explain here why this isn't a 404. IIRC it's essentially because that's simply not true of the real world and also because your calling code may treat that as a permanent failure, not sure exactly. Either way, for a read, callers likely already handle the 404 scenario here (or should) and this is probably what many will want. If we make the response configurable, globally or per route, people can set it in a way that makes sense, in the mean time we should articulate here why a read that is essentially failed returns a 200. We cover that a little in the designing your responses section in delivery.md, maybe that belongs here.

[comment: Your recollection is right, and the case is stronger than "not true of the real world". There are three reasons and they stack:

  - A 404 asserts the resource does not exist. Hyperwyc does not know that, and it is usually false - the resource exists, we just cannot reach it. Asserting it on the server's behalf is the same category of overreach as stamping a Content-Type on a route we know nothing about, which this page already declines to do two sections down.
  - It is permanent, and callers treat it that way. EnsureSuccessStatusCode throws on it, GetFromJsonAsync throws on it, and application code caches "this one does not exist". A read that failed because of connectivity is the most transient condition there is, and encoding it as the most permanent status is backwards.
  - It puts the exception back. The null body exists precisely so that an offline read returns rather than throws; a 4xx hands the throw straight back through the JSON extension methods, undoing the thing the body was chosen for.

503 fails on the first of those too - it asserts something about the server, and the server is fine. So "this request produced no data" is the only honest statement available, and 200 plus null plus X-Hyperwyc-Status: Offline is the only combination that makes it without asserting something false.

Agreed the reasoning should live here. delivery.md's "Why no data instead of no connection" box should then link to it rather than duplicate it - that box is making the application-design argument (your code already handles the empty path) and this is making the status-code argument, and they are genuinely different claims that currently blur together.

On configurability: same answer as the 202 above, and for the sharper of the two reasons. A consumer who sets this to 404 has re-armed the exception for every offline read in their app, which is the exact failure the design exists to prevent.]

## Headers

| Header                      | Present on               | Value                                              |
| --------------------------- | ------------------------ | -------------------------------------------------- |
| `X-Hyperwyc-Status`         | Every synthetic response | `Queued` or `Offline`                              |
| `X-Hyperwyc-Correlation-Id` | The `202`                | The id this write's outcome will be reported under |

`X-Hyperwyc-Correlation-Id` is on every `202`, whether you supplied the value through `HyperwycRequestOptions.CorrelationId` or Hyperwyc generated it — one rule rather than "only when we minted it". It is your key, carrying your meaning; Hyperwyc neither requires it to be unique nor deduplicates on it. See [Knowing which request an event is about](events.md#knowing-which-request-an-event-is-about).

These two are the only headers Hyperwyc adds anywhere, and it adds them only to responses it invents. **Nothing is added on the wire.** A replayed write carries exactly the headers it was made with, so Hyperwyc asks nothing of your API and your server never learns it is there.

## Body

Both synthetic responses carry the four characters `null` — the JSON null literal — with **no `Content-Type`**. An empty body would throw `JsonException` out of `GetFromJsonAsync<T>`; `null` deserialises to `null`, which is a case your code handles anyway. No media type is asserted because Hyperwyc does not know what the route serves. The reasoning is in [Designing your responses](delivery.md#designing-your-responses).

## A cached response is not stamped

A response served from the cache comes back with the origin's captured status code, headers and body, unchanged. There is no header marking it as cached, and no way to tell from the response alone.

**Hyperwyc stamps responses it invents, not responses it remembers.** A cached response is a real server response that arrived earlier, and it is only ever served inside the TTL you set, so by the policy you wrote, it is current. Marking it would be a claim about freshness that the policy has already answered. A Service Worker behaves the same way: a `caches.match()` hit is returned as the stored `Response`, with nothing added.

If you want to observe the cache rather than infer it, `OnUpdated` fires whenever a stored response is refreshed — see [Events](events.md).

## When Hyperwyc doesn't answer at all

In these cases there is no synthetic response, and the caller gets whatever the transport gives — usually an `HttpRequestException`, exactly as if Hyperwyc were not installed:

| Case                                                                                  | Why                                                                                        |
| ------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------ |
| A **write** to a [`NetworkOnly`](delivery.md) route, offline or on a failed transport | The route declared that deferring is the wrong answer, so Hyperwyc does not take custody   |
| The write could not be taken into custody — the store could not be written, or the body could not be read | Nothing is holding the write, so answering `202` would promise something nobody is keeping |
| The store could not be read at all                                                    | Hyperwyc [steps aside for the session](storage.md)                                         |
| A write whose transport failed **after** a connection was made                        | The server may have processed it; see [offline writes](offline-writes.md)                  |

An offline **read** on a `NetworkOnly` route is the exception: it gets the `200`/`Offline` response, because there is nothing to pass through to.
