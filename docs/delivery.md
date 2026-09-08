# Delivery and Route Policies

This document explains where a response comes from, how long it stays usable, and how to vary both per route.

Hyperwyc sits in your `HttpClient` pipeline as a `DelegatingHandler`, the same interception point that a Service Worker occupies for browser `fetch()`. It transparently handles all outgoing requests:

- **Online:** Requests are sent immediately. Responses are optionally cached according to your staleness policy.
- **Offline writes:** Requests are serialised and queued locally. The caller receives a [`202 Accepted` with an `X-Hyperwyc-Status: Queued` header](responses.md). When connectivity is restored, the queue is replayed in order. The eventual outcome arrives on [`Events`](events.md), correlated back to the write that produced it.
- **Offline reads:** Served from cache if it is still within its TTL. Otherwise the caller receives a [`200 OK` with `X-Hyperwyc-Status: Offline`](responses.md) and a body of `null`. A read whose transport cannot answer is treated identically, whatever the connectivity service claimed, so a read never throws where being offline would not have thrown.
- **Online reads (GET/HEAD/OPTIONS):** Served from cache if fresh; fetched from the API if stale or missing.

## Route Policies

Route policies affect how Hyperwyc handles read (`GET`, `OPTIONS`, `HEAD`) and write (`POST`, `PUT`, `PATCH`, `DELETE`) requests when online and offline.

### Reads

Hyperwyc lets you control when responses should be prioritised from the cache over the network, and vice versa.

| Strategy                        | Online                                                                                | Offline                                                        |
| ------------------------------- | ------------------------------------------------------------------------------------- | -------------------------------------------------------------- |
| `RoutePolicy.CacheFirst()`      | Serve a cached response if within the default TTL (1 day); otherwise fetch            | Serve the cached response even if stale                        |
| `RoutePolicy.CacheFirst(ttl)`   | As above, but with a defined TTL                                                      | Serve the cached response if within TTL, otherwise return null |
| `RoutePolicy.NetworkFirst()`    | Always fetch; fall back to the cache only if the request fails                        | Serve the cached response even if stale                        |
| `RoutePolicy.NetworkFirst(ttl)` | Always fetch; fall back to the cache only if the request fails and the cache is fresh | Serve the cached response if within TTL                        |
| `RoutePolicy.NetworkOnly()`     | Always fetch; never read or write the store                                           | Does not return null, allows HttpClient to throw               |

Note that Hyperwyc *always* refreshes the cache and resets the TTL upon a successful network fetch (except for `NetworkOnly`).

[comment: Two rows of the table above are wrong against the code.

"Serve the cached response even if stale" for CacheFirst() and NetworkFirst(): the TTL always applies offline. ServeReadWithoutNetworkAsync checks IsStale unconditionally, and CacheFirst() is not "no TTL" - it is the default TTL of one day. So rows 1/2 and rows 3/4 differ only in where the TTL comes from, not in whether one applies. This also contradicts "Understanding TTL" below, which says, correctly, that it means the same thing online and offline.

"NetworkOnly offline: does not return null, allows HttpClient to throw": an offline *read* on a NetworkOnly route gets the 200/Offline synthetic response. ServeReadWithoutNetworkAsync returns Offline() before it ever reaches the store, and responses.md documents this as the deliberate exception - NetworkOnly governs writes, and on an offline read there is nothing to pass through to. As written, this page and responses.md say opposite things about the same case.]

### Writes

Currently Hyperwyc behaves the same way when online irrespective of strategy: it attempts the make the request, and returns the response if one is received. It doesn't matter what the response is or if the request failed; that's none of Hyperwyc's business, it just returns the response to the caller.

**Hyperwyc queues the request to send later *only* if a response is not received.** A request that failed on the API side was still sent successfully, and as a transport layer tool, Hyperwyc is no longer needed. A request that *failed to send* is Hyperwyc's business, so a failure due to a connectivity or unknown fault is queued.

[comment: "a connectivity or unknown fault is queued" - unknown faults specifically are not. HttpRequestError.Unknown is one of the cases NeverReachedTheApi deliberately excludes, alongside InvalidResponse, ResponseEnded and HttpProtocolError, on the grounds that a connection may have been made and the server may have processed the request. Only four errors queue: NameResolutionError, ConnectionError, SecureConnectionError, ProxyTunnelError. offline-writes.md has this right.]

[comment: "Hyperwyc behaves the same way when online irrespective of strategy" is contradicted by the last row of the table below, where NetworkOnly does not queue on a transport failure. It needs "except NetworkOnly" - and that row is the only place NetworkOnly's write behaviour shows up on the online side, which is easy to miss.]

| Strategy                        | Online                                                                           | Offline                                                |
| ------------------------------- | -------------------------------------------------------------------------------- | ------------------------------------------------------ |
| `RoutePolicy.CacheFirst()`      | Sends the request as normal, queues if the request failed to send                | Queues the request to be processed when online         |
| `RoutePolicy.CacheFirst(ttl)`   | Sends the request as normal, queues if the request failed to send                | Queues the request to be processed when online         |
| `RoutePolicy.NetworkFirst()`    | Sends the request as normal, queues if the request failed to send                | Queues the request to be processed when online         |
| `RoutePolicy.NetworkFirst(ttl)` | Sends the request as normal, queues if the request failed to send                | Queues the request to be processed when online         |
| `RoutePolicy.NetworkOnly()`     | Sends the request as normal, *does not queue even if the request failed to send* | Does not queue the request, allows HttpClient to throw |

## Per-route policies

The default (TODO: what is the default?) applies to everything. Override it per route, **registering from general to specific — each rule refines the ones before it**:

[comment: The default is RoutePolicy.CacheFirst(): SourcePriority.CacheFirst, a TTL of one day, InvalidateCacheOnWrite true. It lives on options.Routes.Default, which is the property getting-started.md assigns to and which this page never names - worth stating both here, since this is the page that owns route policy.]

```csharp
services.AddHyperwyc(options =>
{
    options.Routes
        .For("/api/*",           RoutePolicy.CacheFirst(TimeSpan.FromHours(1)))
        .For("/api/sales/*",     RoutePolicy.NetworkFirst(TimeSpan.FromSeconds(30)))
        .For("/api/payments/*",  RoutePolicy.NetworkOnly());
});
```

It's the same model as `.gitignore` and the CSS cascade: state the general rule, then carve out the exceptions. Where two patterns both match, the one registered later applies, so a broad rule placed *after* a narrow one will override it.

Patterns match on the URL **path** only — scheme, host, port and query string are ignored, so a pattern works whatever your `BaseAddress` is. `/api/sales/*` covers `/api/sales` and everything beneath it; `*` matches everything; matching is case-insensitive.

[comment: Verified: last registration wins - PolicyFor walks the list backwards - so this page and RoutePolicyMap's own docs are correct. But HyperwycOptions.Routes' XML doc says the opposite: "matched first-registered-wins" and "Register most specific first". That is the documentation a consumer sees on the property they configure, and following it produces the wrong policy, silently. RoutePolicy's XML says "first match wins" too, which is technically true of a backwards walk and reads as the opposite. Filed - it is a real defect rather than a wording preference.]

A policy carries three things:

| Member                   | Default      |                                                                               |
| ------------------------ | ------------ | ----------------------------------------------------------------------------- |
| `Strategy`               | `CacheFirst` | How reads are served                                                          |
| `Ttl`                    | 1 day        | How old a stored response may be and still be served, online or offline       |
| `InvalidateCacheOnWrite` | `true`       | Whether a successful write clears stored responses under the same path prefix |

Use the factory methods (as per [Route Policies](#route-policies)) and compose with `with` for anything the factories don't cover:

```csharp
.For("/api/audit/*", RoutePolicy.CacheFirst(TimeSpan.FromDays(7)) with { InvalidateCacheOnWrite = false })
```

> **`NetworkOnly` governs writes as well as reads.** It's the one strategy that does. An offline
> write to a `NetworkOnly` route is **not queued** — it goes to the transport and fails as it
> would without Hyperwyc installed. Use it where deferring a write is the wrong answer even
> though deferring a read would be fine: a payment, a seat reservation, anything contending for a
> shared mutable resource.

## Understanding TTL

TTL (time to live) in a Hyperwyc cache defines *whether or not a response is still valid*, not whether to refetch.

It means the same thing online and offline. Once it expires, the response is not served: online it is refetched, offline the caller gets the same "no data" answer as if nothing were cached. So set it to how long the data is genuinely useful, not to how often you would like to refresh. "Always fetch when I can" is `NetworkFirst`, which is a strategy; using a short TTL to force refetching would leave you nothing to serve offline, which is the opposite of the point. The default is one day.

`NetworkOnly` opts out of the store entirely, so it has nothing to offer offline.

The app doesn't need to know the difference. Your existing code doesn't change.

[comment: This section is the clearest writing in the docs, and it answers a question the tables 40 lines above it raise. That distance is the progressive-disclosure problem in miniature: "Understanding TTL" and "Designing your responses" are explanation, the tables are reference, and a reader needs them in the opposite order depending on which one they came here for.]

> **Why "no data" instead of "no connection"?** Connectivity is an infrastructure concern, not
> an application one. Your code already has to handle the empty-result path (a search with no
> matches, a feed with no items); offline simply produces the same shape. A caller that does want
> to know reads the [`X-Hyperwyc-Status` header](responses.md) — or, for a write, the `202`, which
> no ordinary success is.

TODO: I wonder if I should add an "opinions" doc, or similar. Philosophy maybe? Hyperwyc has in many cases deliberately got out of the way and done as much as possible to stay in its lane. In other places it holds strong opinions, this is one example. This one is entirely defensible for two reasons; the first is that it is almost inarguably true, the issue is not _that_ your app has to handle the empty result path, it's that it _should_ handle it at the application logic layer rather than the infrastructure layer, but it is still true, and the second is that the header provides the infrastructure layer handling for those that want it. The impact of either is that if your code currently catches and handles an HTTP client exception in the application logic (say a ViewModel), the premise that "you don't have to change your calling code" no longer holds true. Granted, that cannot hold absolutely true universally, but the point is that Hyperwyc is expressing an opinion here that, if you're handling infrastructure in your application code, you're doing it wrong. The following section also makes a case for the result pattern, and during development (not sure if this is captured) I thought about Hyperwyc driving you to adopt distributed patterns (e.g. considering requests accepted rather than completed, with the event stream providing the equivalent of eventual consistency), and I wonder whether these should be recorded and shared somewhere.

[comment: Agreed, and I would go further: two pages rather than one, because they are different commitments and a reader wants them on different days.

Design principles (or philosophy) holds the stances - infrastructure is not application logic, a queued write is submitted rather than successful, why null and not an empty body, why a 200 and not a 404, why no retry, why no deduplication. A reader can disagree with every one of these and still use the library correctly.

Patterns holds what to actually build - the application-owned store, correlating a 202 back to a local record, marking unsynced and then synced from the event stream, a "sync now" affordance. That is load-bearing, and it is already specified: it is backlog item 50, "Designing resilient applications with Hyperwyc", down to the inspection-app worked example. So patterns is not a new document, it is item 50 finally being written, and this TODO is the argument for scheduling it rather than leaving it unscheduled.

Your point about the calling code is the sharpest thing in this note and it belongs in the principles page close to verbatim. The promise is "you don't have to change your calling code", and it stops being true for an app that catches HttpRequestException in a ViewModel - precisely because Hyperwyc's whole point is that the exception stops happening. That is not a caveat to bury; it is the clearest statement of the opinion you could make, and stating it plainly makes the opinion easier to accept rather than harder.

On the distributed-patterns thread: I do not think it is captured anywhere. The closest is events.md's collapsed "framing note" about submitted versus successful, which is one paragraph hidden behind a details element. Requests as accepted rather than completed, with the event stream as the eventual-consistency channel, is the same idea stated properly, and it is the through-line that makes the 202, the correlation id, the event stream and the "no data" read all one design rather than four decisions.]

## Designing your responses

When Hyperwyc has nothing to give you, like an offline read with no cached copy, or a write it has only queued, it returns the JSON `null` literal, not an empty body. That distinction matters more than it looks:

```csharp
var product  = await http.GetFromJsonAsync<Product>("/products/1");        // null
var products = await http.GetFromJsonAsync<List<Product>>("/products");    // null
```

This is deliberate, it allows `HttpClient` to return a result, even if null, rather than throwing an exception, and it works both with and without the JSON extension methods.

Both return `null` rather than throwing. An *empty* body would be fine for a plain `HttpClient`, but would throw `JsonException` from inside the extension method, for either a single object or a collection, because an empty body is not "no data", it is not valid JSON at all.

```csharp
var products = await http.GetFromJsonAsync<List<Product>>("/products");

if (products is null)
{
    // Offline with nothing cached. Show an empty state, or check
    // X-Hyperwyc-Status if you want to say why.
    return [];
}
```

In almost every case your code already needs to handle a `null` result, whether using the JSON extension methods, using plain `HttpClient`, or even calling other API types (like SOAP/XML). At some point you need to deserialise the response, which can return `null` (there are no .NET generic deserialisers that are not nullable), or you need to read string content. This last scenario is the only one where you may need to do something different - if you are reading literal string content, your code needs to check for the exact string match `null`.

[comment: "there are no .NET generic deserialisers that are not nullable" is a claim about the whole ecosystem and is easy to falsify - a source-generated context or a custom converter can return non-nullable. The narrower claim carries the argument and is simply true: JsonSerializer.Deserialize<T> and the HttpClient JSON extension methods all return T?.]


**A collection comes back as `null`, not empty.** This may be different from what your API returns. Returning `[]` would need Hyperwyc to know the route returns a collection, which is knowledge it does not have. A null-coalesce at the call site covers it, and you need one for the online path anyway; while your API may guarantee an empty array, `HttpClient` does not.

If you would rather branch on status codes than on `null`, read [`X-Hyperwyc-Status`](responses.md) to find out. And if your API already uses an envelope or result type — [`Ardalis.Result`](https://github.com/ardalis/Result) or a hand-rolled `ApiResponse<T>` — that keeps working, since the envelope simply deserialises to `null` and your existing handling takes over.
