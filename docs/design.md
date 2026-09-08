# Design

Why Hyperwyc behaves the way it does, gathered in one place so the reference pages can get on with saying what it does.

Nothing here is required reading. It is the page for when a decision looks odd and you want to know whether it was considered, and it is where Hyperwyc's opinions live, so you can disagree with them deliberately rather than by accident. The formal records are in[Architecture decisions](decisions/); this is the readable version.


## Connectivity is an optimisation, not a correctness input

The most consequential thing to understand, because everything else is derived from it.

Hyperwyc uses an `IConnectivityService` to determine which path to try first. If the connectivity service reports offline, *Hyperwyc honours this and short circuits the HTTP request*. When `IConnectivityService` reports online, Hyperwyc will attempt to make the request, and where a request is actually attempted, the transport is what knows: a write whose connection is refused is queued, whatever the connectivity service claimed, and a read whose transport cannot answer is served from the store on the same evidence.

This makes the connectivity source a question of efficiency rather than of correctness, which is why the library works with no configuration at all.

**The claim has a bound, and it is worth being precise about it.** The transport only gets the last word where it is consulted:

| Reported | Actual  | Outcome                                                                                                                                   |
| -------- | ------- | ----------------------------------------------------------------------------------------------------------------------------------------- |
| Online   | Offline | The request is attempted and fails. A read is served from the store; a write is queued. **Self-correcting** — it costs one failed request |
| Offline  | Online  | Hyperwyc answers before the transport is reached. **Not self-correcting** — nothing contradicts it, because nothing was asked             |

Nothing is lost in either direction, but the second costs freshness and delays a write until the next connectivity change, and an implementation *stuck* reporting offline never raises one, while returning `202`s that look like success.

So: **prefer erring toward connected**. The shipped fallback does, which is what makes it safe to be the default. See [ADR 0007](decisions/0007-connectivity-cannot-cost-correctness.md), and [ADR 0006](decisions/0006-a-shipped-implementation-is-not-a-default.md) for why an implementation ships without being registered.

## Why not just probe the API?

The obvious improvement is to make the check smarter — ping your API, or resolve its host name, and report *that*. Pinging is expensive and roughly duplicates the request your app is about to make anyway. Resolving looks cheaper but is actually worse, for several reasons:

* **You cannot guarantee a fresh lookup.** `Dns.GetHostEntry` goes through the OS resolver, which caches — the DNS Client service on Windows, `systemd-resolved` on most Linux — and there is no "bypass the cache" flag. Being sure would mean speaking DNS yourself to a chosen server over UDP/53: a dependency, and a port that is routinely blocked or intercepted.
* **Negative caching makes it fail the wrong way.** `NXDOMAIN` and `SERVFAIL` are cached too, for the zone's SOA minimum. A lookup that failed while you were offline keeps failing after the network returns, so connectivity reports offline, the flush never fires, and queued writes sit there. A false positive costs one wasted attempt; a false negative costs delivery. This trades the cheap failure for the expensive one.
* **You cannot even test whether resolution is working.** `Dns.GetHostEntry` throws `SocketException` both when a name does not exist and when no resolver can be contacted, so resolving a deliberately-absent name tells you nothing. `SocketErrorCode` nominally separates `HostNotFound` from `TryAgain`, but which you get depends on the platform's resolver and volatile configuration.
* **And a captive portal defeats it in the wrong direction.** A portal has to answer DNS in order to redirect you, so resolution *succeeds* behind one while your API stays unreachable.

Which leaves the conclusion the design already assumes: the only reliable test of whether your API is reachable is a request to your API. Hyperwyc makes that test every time it tries.

## Why a `200` and not a `404`

An offline read with nothing to serve returns `200 OK`, a body of `null`, and `X-Hyperwyc-Status: Offline`. Not a `404`, and not a `503`. Three reasons, and they stack:

- **A `404` asserts the resource does not exist.** Hyperwyc does not know that, and it is usually false — the resource is there, we cannot reach it. Asserting it on the server's behalf is the same overreach as stamping a `Content-Type` on a route we know nothing about, which Hyperwyc also declines to do.
- **It is permanent, and callers treat it that way.** `EnsureSuccessStatusCode` throws on it, `GetFromJsonAsync` throws on it, applications cache "this one does not exist". A read that failed because of connectivity is the most transient condition there is; encoding it as the most permanent status is backwards.
- **It puts the exception back.** The `null` body exists precisely so an offline read returns rather than throws. A 4xx hands the throw straight back through the JSON extension methods, undoing the thing the body was chosen for.

`503` fails the first of those too — it asserts something about the server, and the server is fine. "This request produced no data" is the only honest statement available, and `200` + `null` + a header is the only combination that makes it without asserting something false.

## Connectivity is infrastructure, and infrastructure is not application logic

This is an opinion. It is defensible, and it is worth stating plainly rather than leaving implicit.

Your code already has to handle the empty-result path: a search with no matches, a feed with no items. Being offline produces the same shape, so it needs no new branch. A caller that *does* want to know reads `X-Hyperwyc-Status` or, for a write, the `202`, which no ordinary success is.

The opinion underneath is that if you are handling infrastructure failures in your application logic, that is the thing to change. Not because it will not work, but because a ViewModel catching `HttpRequestException` is a ViewModel that knows about sockets.

**And here is the cost, because the README makes a promise this qualifies.** "You don't have to change your calling code" stops being true for an application that catches `HttpRequestException` in a ViewModel and does something meaningful with it — precisely *because* Hyperwyc's whole point is so that you don't have to (and shouldn't). That code will not break; it will stop running, which can be worse. If your error handling lives above the transport, look at it before you ship.

## A queued write is submitted, not successful

Surfacing outcomes on an event stream nudges you toward describing the action rather than its result — "order **submitted**", not "order **successful**" — with the outcome arriving separately and later.

This is similar to the distributed-systems pattern many teams already apply to their backend and have never carried into the client: a request is *accepted*, and consistency is eventual. The `202`, the correlation id, the event stream and the "no data" read are one design rather than four decisions, and this is what joins them up.

It is genuinely optional — Hyperwyc does not require anyone to model their UI a particular way. But an application that models a deferred write as complete is telling its user something Hyperwyc did not tell it.

The concrete version of this pattern — an application-owned store alongside Hyperwyc's, reconciled from the event stream — is the one thing this page does not yet cover, and it is the most useful thing to write next.

## `null` rather than an empty body

When Hyperwyc has nothing to give you it returns the four characters `null`, with no `Content-Type`.

An *empty* body would be fine for a plain `HttpClient` but throws `JsonException` out of `GetFromJsonAsync<T>`, for a single object every bit as much as for a collection, because an empty body is not "no data" — it is not JSON at all. `null` deserialises to `null`, which is a case your code handles anyway.

No media type is asserted because Hyperwyc does not know what the route serves; it may be SOAP, XML or protobuf. `GetFromJsonAsync<T>` does not inspect the content type, so nothing is lost.

**A collection comes back as `null`, not empty.** Returning `[]` would need Hyperwyc to know the route returns a collection, which is per-route knowledge it does not have. A null-coalesce at the call site covers it, and you need one for the online path anyway — your API may guarantee an empty array, but `HttpClient` does not.

## Hyperwyc does not retry

A write the server *answers* is finished with, whatever it said — a `500`, a `429` and a `503` included. Hyperwyc's job is to get the request to your API, and a response means it succeeded.

It has three triggers: application start, connectivity restored, and an explicit `FlushAsync()`. None of them correlates with a change to the condition which resulted in a non-success response. Requeuing a `503` schedules a retry on an unrelated event, and on a device that never goes offline again it schedules one that never arrives.

Meanwhile your pipeline has already had the better option. A replay traverses it, so a resilience handler such as Polly retries on a schedule that tracks the actual failure, with backoff and `Retry-After`, before Hyperwyc sees the result. A second, worse retry on top would duplicate a job that has an owner. See [ADR 0001](decisions/0001-idempotency-is-not-hyperwycs-remit.md).

## Hyperwyc takes no position on duplicates

Any retry can deliver the same request twice: if a response is lost after the server has committed, the retry looks identical to a first attempt. That is true of a Polly retry, a user double-tapping a button, and a proxy replaying a request. Hyperwyc's replay carries the same risk and no more.

It sends no headers of its own on the wire and asks nothing of your API. Duplicate suppression is between your application and your backend — a client-generated domain identity, an `Idempotency-Key` you set at the call site, or a correlation id you already emit. Which one fits, or whether the concern applies at all, depends on your solution.

## An unreadable store is reported, not repaired

> **Note:** [backlog item 62](../Backlog/62-reset-store-on-failure.md) is under consideration and may change this scenario, but it is true at time of writing.

If the store cannot be read, Hyperwyc logs it, publishes `OnStoreUnreadable` once, and passes every request straight through as though it were not installed. Nothing is deleted and nothing is thrown.

A damaged store is still your data, and deleting it is irreversible. Refusing to start is a decision your application might reasonably make, but Hyperwyc has no standing to make it for you.

Offline writes are *declined* rather than accepted in that state: with no store to hold them, a `202` would promise delivery Hyperwyc cannot keep, so the request goes to the transport and fails as it would without Hyperwyc there. That failure is visible and recoverable; a lost `202` is neither.

## Further

- **[Architecture decisions](decisions/)** — the formal records, kept so a future decision of the same shape can be answered consistently rather than re-argued. Worth reading if you are wondering why some obvious feature is absent.
- **[Is Hyperwyc right for your app?](choosing.md)** — the scope question, from the other end.
