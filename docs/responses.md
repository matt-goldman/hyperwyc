# Synthetic responses

What a caller sees when Hyperwyc answers instead of the server: the status codes, the two headers, and the body.

Hyperwyc answers a request itself in exactly two cases:

| Case                                                | Status         | `X-Hyperwyc-Status` |
| --------------------------------------------------- | -------------- | ------------------- |
| A write was queued for later delivery               | `202 Accepted` | `Queued`            |
| A read requested while offline had nothing to serve | `200 OK`       | `Offline`           |

Either can arise two ways: the connectivity service reported offline, or the request was attempted and [the transport could not answer](offline-writes.md#writes-are-queued-on-transport-failure-too-not-just-when-you-are-offline). **Both produce the same response** (depending only on whether the request is a read or a write) — the caller cannot tell which happened, and does not need to. Why that is safe either way is in [Design](design.md#connectivity-is-an-optimisation-not-a-correctness-input).

Everything else your caller sees — including a cache hit — is a real server response, returned unchanged.

## Status codes

**`202 Accepted` — the write is queued.** Not `200`, because the request has been accepted for later processing and has not yet been performed against the origin server. It is also how a caller tells a queued write from a delivered one without reading a header, but only if no ordinary success from your API is a `202`. The code is fixed, and deliberately not configurable: it is the only in-band signal separating a queued write from a delivered one.

**`200 OK` — the read found nothing.** Offline with no cached copy, or a cached copy past its [TTL](delivery.md). Not a `404` and not a `503`, both of which would assert something Hyperwyc does not know and put the exception back — see [why a `200`](design.md#why-a-200-and-not-a-404). Your code handles the empty result the same way it already handles a search with no matches.

## Headers

| Header                      | Present on               | Value                                              |
| --------------------------- | ------------------------ | -------------------------------------------------- |
| `X-Hyperwyc-Status`         | Every synthetic response | `Queued` or `Offline`                              |
| `X-Hyperwyc-Correlation-Id` | The `202`                | The id this write's outcome will be reported under |

`X-Hyperwyc-Correlation-Id` is on every `202`, whether you supplied the value through `HyperwycRequestOptions.CorrelationId` or Hyperwyc generated it — one rule rather than "only when we minted it". It is your key, carrying your meaning; Hyperwyc neither requires it to be unique nor deduplicates on it. See [Knowing which request an event is about](events.md#knowing-which-request-an-event-is-about).

These two are the only headers Hyperwyc adds anywhere, and it adds them only to responses it invents. **Nothing is added on the wire.** A replayed write carries exactly the headers it was made with, so Hyperwyc asks nothing of your API and your server never learns it is there.

## Body

Both synthetic responses carry the four characters `null` — the JSON null literal — with **no `Content-Type`**. An empty body would throw `JsonException` out of `GetFromJsonAsync<T>`; `null` deserialises to `null`, which is a case your code handles anyway. No media type is asserted because Hyperwyc does not know what the route serves. The reasoning is in [what a caller gets when there is nothing to give](delivery.md#what-a-caller-gets-when-there-is-nothing-to-give).

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
