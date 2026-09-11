# Design

Why Hyperwyc behaves the way it does, gathered in one place so the reference pages can get on with saying what it does.

Nothing here is required reading. It is the page for when a decision looks odd and you want to know whether it was considered, and it is where Hyperwyc's opinions live, so you can disagree with them deliberately rather than by accident. The formal records are in [Architecture decisions](decisions/); this is the readable version.


## Delivery is what Hyperwyc succeeds or fails at

The first thing to get straight, because HTTP has its own success and failure axis running at right angles to this one, and the two use the same words.

- **Success is delivery.** The request reached your API and your API answered. That is the whole of what Hyperwyc promised.
- **Failure is non-delivery.** Nothing was sent, so the request is kept and tried again when there is some prospect of it working.
- **The outcome of a delivery is not Hyperwyc's concern.** A `201` and a `409` are the same event from here: your API received the request and said something back. What it said is a conversation between your application and your backend, and Hyperwyc has no standing in it.

Almost everything else on this page follows from that. It is why a `503` is not retried — the server answered, so the delivery is done, and the fact that you did not like the answer is not a network condition. It is why the outbox empties on any response rather than only on a `2xx`. And it is why the `202` means *accepted for delivery*, not *accepted by your API*.

It is also why the store keeps nothing once a write has gone out. Every delivery leaves, whatever the answer was: the [event](events.md) tells you what the server said, and keeping that is your application's job. [ADR 0010](decisions/0010-delivery-ends-hyperwycs-interest.md) is the record of that decision, and is honest about what it costs you.

## Connectivity is an optimisation, not a correctness input

The most consequential thing to understand, because everything else is derived from it.

Hyperwyc uses an `IConnectivityService` to determine which path to try first. If the connectivity service reports offline, *Hyperwyc honours this and short circuits the HTTP request*. When `IConnectivityService` reports online, Hyperwyc will attempt to make the request, and where a request is actually attempted, the transport is what knows: a write whose connection is refused is queued, whatever the connectivity service claimed, and a read whose transport cannot answer is served from the store on the same evidence.

This makes the connectivity source a question of efficiency rather than of correctness, which is why the library works with no configuration at all.

**The important consequence of this** is that the transport only gets the last word when it is consulted:

| Reported | Actual  | Outcome                                                                                                                                   |
| -------- | ------- | ----------------------------------------------------------------------------------------------------------------------------------------- |
| Online   | Offline | The request is attempted and fails. A read is served from the store; a write is queued. **Self-correcting** — it costs one failed request |
| Offline  | Online  | Hyperwyc answers before the transport is reached. **Not self-correcting** — nothing contradicts it, because nothing was asked             |

Nothing is lost in either direction, but the second costs freshness and delays a write until the next connectivity change, and an implementation *stuck* reporting offline never raises one, while returning `202`s that look like success.

So: **prefer erring toward connected**. The shipped fallback does, which is what makes it safe to be the default. See [ADR 0007](decisions/0007-connectivity-cannot-cost-correctness.md), and [ADR 0006](decisions/0006-a-shipped-implementation-is-not-a-default.md) for why an implementation ships without being registered.

## Why not just probe the API?

The obvious improvement is to make the check smarter, e.g.just ping your API, or resolve its host name, and report the result. Pinging is expensive and roughly duplicates the request your app is about to make anyway. Resolving looks cheaper but is actually worse, for several reasons:

* **You cannot guarantee a fresh lookup.** `Dns.GetHostEntry` goes through the OS resolver, which caches (the DNS Client service on Windows, `systemd-resolved` on most Linux) and there is no "bypass the cache" flag.
* **Negative caching makes it fail the wrong way.** `NXDOMAIN` and `SERVFAIL` are cached too, for the zone's SOA minimum. A lookup that failed while you were offline keeps failing after the network returns, so connectivity reports offline, the flush never fires, and queued writes sit there. A false positive costs one wasted attempt; a false negative is a potentially permanent error, which trades the cheap failure for the expensive one.
* **You cannot even test whether resolution is working.** `Dns.GetHostEntry` throws `SocketException` both when a name does not exist and when no resolver can be contacted, so resolving a deliberately-absent name tells you nothing. `SocketErrorCode` nominally separates `HostNotFound` from `TryAgain`, but which you get depends on the platform's resolver, and configuration way outside your control.
* **And a captive portal defeats it in the wrong direction.** A portal has to answer DNS in order to redirect you, so resolution *succeeds* behind one while your API stays unreachable.

In short, probing DNS with any degree of confidence would require a bespoke DNS probe (not using the BCL tools) that you write yourself, to a chosen server over UDP/53: a dependency, and a port that is routinely blocked or intercepted.

Which leaves the conclusion the design already assumes: the only reliable test of whether your API is reachable is a request to your API. Hyperwyc makes that test every time it tries, which makes a prior, ad-hoc, test call first pointless.

## Why a `200` and not a `404`

An offline read with nothing to serve returns `200 OK`, a body of `null`, and `X-Hyperwyc-Status: Offline`. Not a `404`, and not a `503`. Three reasons, and they stack:

- **A `404` asserts the resource does not exist.** Hyperwyc does'nt know that (and it is usually false; the resource is there, we just can't reach it). Asserting it on the server's behalf is the same overreach as stamping a `Content-Type` on a route we know nothing about, which Hyperwyc also declines to do.
- **It is permanent, and callers treat it that way.** `EnsureSuccessStatusCode` throws on it, `GetFromJsonAsync` throws on it, applications cache "this one does not exist". A read that failed because of connectivity is the most transient condition there is; encoding it as the most permanent status is backwards.
- **It puts the exception back.** The `null` body exists precisely so an offline read returns rather than throws. A 4xx hands the throw straight back through the JSON extension methods, undoing the thing the body was chosen for.

`503` fails the first of those too as it asserts something about the server, and the server is fine (probably; we don't know, and that's the point). The only thing we can say with honest is that the request produced no data, and the `200` status code, `null` body and header does so without asserting something it can't know.

## Connectivity is infrastructure, and infrastructure is not application logic

Hyperwyc is deliberately naive and agnostic about how you choose to use it, declining to impose anything on you wherever possible. However, Hyperwyc does have a strong opinion about this one thing. It probably doesn't need defending, but given that:

a) it is easily defensible, and 
b) is the one place where Hyperwyc either aligns with your existing opinion on the matter, or imposes it on you (or Hyperwyc just isn't for you, and that's OK too)

it merits an explicit statement here rather than leaving implicit.

In your application logic, your code already has to handle the empty-result path: a search with no matches, a feed with no items. Being offline results in the same thing, so it needs no new branch. In Hyperwyc, a caller that *does* want to know can read `X-Hyperwyc-Status` or, for a write, the `202` (but a write can still use the header if `202` is an expected success status code from your API).

The opinion underneath is that if you are handling infrastructure failures in your application logic, that is the thing to change. Not because it doesn't work, but because (e.g.) a ViewModel catching `HttpRequestException` is a ViewModel that knows about sockets and a poor separation of concerns.

So the README's claim that "You don't have to change your calling code" is based on the assumption that you agree with this opinion, that application logic doesn't handle infrastructure exceptions, and that this separation of infrastructure and application logic the correct way to structure your code.

It's not true for an application that catches `HttpRequestException` in a ViewModel and does something meaningful with it, precisely *because* Hyperwyc's whole point is so that you don't have to (and shouldn't). If you're catching an HTTP exception a level up (or more) from where `HttpClient` is called, that code will not break if you add Hyperwyc, it will never be hit, which. If your infrastructure error handling lives above the transport, you should review it before integrating Hyperwyc.

## A queued write is submitted, not successful

Surfacing outcomes on an event stream nudges you toward describing the action rather than its result, e.g. "order **submitted**", not "order **successful**", with the outcome arriving separately and later.

This is similar to the distributed-systems pattern many are already familiar with: a request is *accepted*, and consistency is eventual. The `202`, the correlation id, the event stream and the "no data" read are effectively the same pattern.

Whether you adopt this pattern or mental model is entirely up to you, but what's important to know for your implementation is that Hyperwyc doesn't pretend that a request made while offline was successfully processed by the intended destination; in fact it doesn't even know what that looks like. It just tells you it has accepted the request on your behalf and will deliver it when it can.

The important thing is to decide what your calling code needs to know. If simply queuing the request is enough, then you probably don't need to do anything else. If the caller needs to know the eventual outcome of the back end response, then reflect the Hyperwyc status accordingly, and update eventually from the event stream.

## `null` rather than an empty body

When Hyperwyc has nothing to give you it returns the four characters `null`, with no `Content-Type`.

An *empty* body would be fine for a plain `HttpClient` but throws `JsonException` out of `GetFromJsonAsync<T>`, for a collection as well as a single object, because an empty body is not "no data", it is not JSON at all. `null` deserialises to `null`, which is a case your code handles anyway.

No media type is asserted because Hyperwyc does not know what the route serves; it may be SOAP, XML or protobuf. `GetFromJsonAsync<T>` does not inspect the content type, so nothing is lost.

**A collection comes back as `null`, not empty.** Returning `[]` would need Hyperwyc to know the route returns a collection, which is per-route knowledge it does not have. A null-coalesce at the call site covers it, and you need one for the online path anyway; your API may guarantee an empty array, but `HttpClient` does not.

If you're not using the JSON HTTP extensions, you may need to literally handle the `null` string, if you're not already.

## Hyperwyc does not retry

A write the server *answers* is finished with, whatever it said; non-success status codes like `500`, `429` or `503` included. Hyperwyc's job is to get the request to your API, and a response means it succeeded.

Hyperwyc's queue has three triggers: connectivity restored, an explicit call to `FlushAsync()`, and application start if you opt into it. None of them correlates with a change to the condition which resulted in a non-success response. Requeuing a `503` schedules a retry on an unrelated event, and on a device that never goes offline again it schedules one that never arrives.

Meanwhile, your HTTP client handler pipeline has likely already had a better attempt at retrying. A resilience handler such as Polly retries on a schedule that tracks the actual failure, with backoff and `Retry-After`, before Hyperwyc sees the result. A replay traverses the same pipeline, so a second, worse retry on top would duplicate a job that already has an owner. See [ADR 0001](decisions/0001-idempotency-is-not-hyperwycs-remit.md) and ["not" alternatives](choosing.md#not-alternatives).

## Hyperwyc takes no position on duplicates

Any retry can deliver the same request twice: if a response is lost after the server has committed, the retry looks identical to a first attempt. That is true of a Polly retry, a user double-tapping a button, and a proxy replaying a request. Hyperwyc's replay carries the same risk and no more.

It sends no headers of its own on the wire and asks nothing of your API. Duplicate suppression is between your application and your backend, using a client-generated domain identity, an `Idempotency-Key` you set at the call site, or a correlation id you already emit. Which one fits, or whether the concern applies at all, depends on your solution.

## An unreadable store is set aside, not repaired and not deleted

If the store cannot be read, Hyperwyc moves it aside once, starts a clean one, and carries on — logging it and publishing `OnStoreQuarantined`. Nothing is deleted and nothing is thrown, and caching and queueing keep working from the next request onward.

Moved rather than deleted, because a damaged store is still your data and deleting it is irreversible — and the usual cause is a key or path change rather than damage, so the bytes are generally intact and merely unopenable. They stay reachable by whoever holds the key. See [storage.md](storage.md#when-the-store-cant-be-read).

Once, because a store that becomes unreadable a second time is a systemic fault rather than an incident. If a second store becomes unreadable, Hyperwyc falls passthrough: it logs the error, publishes an `OnStoreUnreadable` event, and passes every request straight through as though it were not installed. That also bounds what accumulates on the device, without a sweep or a setting — the existence of the first orphan is the counter.

Refusing to start is a decision your application might reasonably make, but Hyperwyc has no standing to make it for you.

Offline writes are *declined* rather than accepted in that state: with no store to hold them, a `202` would promise delivery Hyperwyc cannot keep, so the request goes to the transport and fails as it would without Hyperwyc there. That failure is visible and recoverable; a lost `202` is neither.

## Further

- **[Architecture decisions](decisions/)** — the formal records, kept so a future decision of the same shape can be answered consistently rather than re-argued. Worth reading if you are wondering why some obvious feature is absent.
- **[Is Hyperwyc right for your app?](choosing.md)** — the scope question, from the other end.
