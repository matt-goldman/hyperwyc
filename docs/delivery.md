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

| Strategy                        | Online                                                                                | Offline                                                        |
| ------------------------------- | ------------------------------------------------------------------------------------- | -------------------------------------------------------------- |
| `RoutePolicy.CacheFirst()`      | Serve a cached response if within the default TTL (1 day); otherwise fetch            | Serve the cached response even if stale                        |
| `RoutePolicy.CacheFirst(ttl)`   | As above, but with a defined TTL                                                      | Serve the cached response if within TTL, otherwise return null |
| `RoutePolicy.NetworkFirst()`    | Always fetch; fall back to the cache only if the request fails                        | Serve the cached response even if stale                        |
| `RoutePolicy.NetworkFirst(ttl)` | Always fetch; fall back to the cache only if the request fails and the cache is fresh | Serve the cached response if within TTL                        |
| `RoutePolicy.NetworkOnly()`     | Always fetch; never read or write the store                                           | Does not return null, allows HttpClient to throw               |

### Writes

Currently Hyperwyc behaves the same way when online irrespective of strategy: it attempts the make the request, and returns the response if one is received. It doesn't matter what the response is or if the request failed; that's none of Hyperwyc's business, it just returns the response to the caller.

**Hyperwyc queues the request to send later *only* if a response is not received.** A request that failed on the API side was still sent successfully, and as a transport layer tool, Hyperwyc is no longer needed. A request that *failed to send* is Hyperwyc's business, so a failure due to a connectivity or unknown fault is queued.

| Strategy                        | Online                                                                           | Offline                                                |
| ------------------------------- | -------------------------------------------------------------------------------- | ------------------------------------------------------ |
| `RoutePolicy.CacheFirst()`      | Sends the request as normal, queues if the request failed to send                | Queues the request to be processed when online         |
| `RoutePolicy.CacheFirst(ttl)`   | Sends the request as normal, queues if the request failed to send                | Queues the request to be processed when online         |
| `RoutePolicy.NetworkFirst()`    | Sends the request as normal, queues if the request failed to send                | Queues the request to be processed when online         |
| `RoutePolicy.NetworkFirst(ttl)` | Sends the request as normal, queues if the request failed to send                | Queues the request to be processed when online         |
| `RoutePolicy.NetworkOnly()`     | Sends the request as normal, *does not queue even if the request failed to send* | Does not queue the request, allows HttpClient to throw |

## Per-route policies

The default (TODO: what is the default?) applies to everything. Override it per route, **registering from general to specific — each rule refines the ones before it**:

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

> **Why "no data" instead of "no connection"?** Connectivity is an infrastructure concern, not
> an application one. Your code already has to handle the empty-result path (a search with no
> matches, a feed with no items); offline simply produces the same shape. A caller that does want
> to know reads the [`X-Hyperwyc-Status` header](responses.md) — or, for a write, the `202`, which
> no ordinary success is.

TODO: I wonder if I should add an "opinions" doc, or similar. Philosophy maybe? Hyperwyc has in many cases deliberately got out of the way and done as much as possible to stay in its lane. In other places it holds strong opinions, this is one example. This one is entirely defensible for two reasons; the first is that it is almost inarguably true, the issue is not _that_ your app has to handle the empty result path, it's that it _should_ handle it at the application logic layer rather than the infrastructure layer, but it is still true, and the second is that the header provides the infrastructure layer handling for those that want it. The impact of either is that if your code currently catches and handles an HTTP client exception in the application logic (say a ViewModel), the premise that "you don't have to change your calling code" no longer holds true. Granted, that cannot hold absolutely true universally, but the point is that Hyperwyc is expressing an opinion here that, if you're handling infrastructure in your application code, you're doing it wrong. The following section also makes a case for the result pattern, and during development (not sure if this is captured) I thought about Hyperwyc driving you to adopt distributed patterns (e.g. considering requests accepted rather than completed, with the event stream providing the equivalent of eventual consistency), and I wonder whether these should be recorded and shared somewhere.

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


**A collection comes back as `null`, not empty.** This may be different from what your API returns. Returning `[]` would need Hyperwyc to know the route returns a collection, which is knowledge it does not have. A null-coalesce at the call site covers it, and you need one for the online path anyway; while your API may guarantee an empty array, `HttpClient` does not.

If you would rather branch on status codes than on `null`, read [`X-Hyperwyc-Status`](responses.md) to find out. And if your API already uses an envelope or result type — [`Ardalis.Result`](https://github.com/ardalis/Result) or a hand-rolled `ApiResponse<T>` — that keeps working, since the envelope simply deserialises to `null` and your existing handling takes over.
