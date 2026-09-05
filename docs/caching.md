# Caching and route policies

Where a response comes from, how long it stays usable, and how to vary both per route.

Hyperwyc sits in your `HttpClient` pipeline as a `DelegatingHandler` — the same interception point that a Service Worker occupies for browser `fetch()`. It transparently handles all outgoing requests:

- **Online:** Requests are sent immediately. Responses are optionally cached according to your staleness policy.
- **Offline writes:** Requests are serialised and queued locally. The caller receives a [`202 Accepted` with an `X-Hyperwyc-Status: Queued` header](responses.md). When connectivity is restored, the queue is replayed in order. The eventual outcome arrives on [`Events`](events.md), correlated back to the write that produced it.
- **Offline reads:** Served from cache if available (even if stale — any data is better than no data offline). If no cache exists, the caller receives a [`200 OK` with `X-Hyperwyc-Status: Offline`](responses.md) and a body of `null`.
- **Online reads (GET/HEAD/OPTIONS):** Served from cache if fresh; fetched from the API if stale or missing.

## Caching strategies

| Strategy | Online | Offline |
|---|---|---|
| `RoutePolicy.CacheFirst()` | Serve a fresh cached response; otherwise fetch | Serve the cached response even if stale |
| `RoutePolicy.CacheFirst(ttl)` | As above, with the freshness window stated | As above |
| `RoutePolicy.NetworkFirst()` | Always fetch; fall back to the cache only if the request fails | Serve the cached response even if stale |
| `RoutePolicy.NetworkOnly()` | Always fetch; never read or write the store | **Writes are not queued** — see below |

## Per-route policies

The default applies to everything. Override it per route, **registering from general to
specific — each rule refines the ones before it**:

```csharp
services.AddHyperwyc(options =>
{
    options.Routes
        .For("/api/*",           RoutePolicy.CacheFirst(TimeSpan.FromHours(1)))
        .For("/api/sales/*",     RoutePolicy.NetworkFirst(TimeSpan.FromSeconds(30)))
        .For("/api/payments/*",  RoutePolicy.NetworkOnly());
});
```

Written out that forms a pyramid — the shortest line at the top, widening as each rule narrows
— which is the order you think in, and a shape you can check at a glance. It's the same model as `.gitignore` and the CSS cascade: state the
general rule, then carve out the exceptions. Where two patterns both match, the one registered
later applies, so a broad rule placed *after* a narrow one will override it.

Patterns match on the URL **path** only — scheme, host, port and query string are ignored, so a
pattern works whatever your `BaseAddress` is. `/api/sales/*` covers `/api/sales` and everything
beneath it; `*` matches everything; matching is case-insensitive.

A policy carries three things:

| Member | Default | |
|---|---|---|
| `Strategy` | `CacheFirst` | How reads are served |
| `Ttl` | 1 day | How old a stored response may be and still be served, online or offline |
| `InvalidateCacheOnWrite` | `true` | Whether a successful write clears stored responses under the same path prefix |

Compose with `with` for anything the factories don't cover:

```csharp
.For("/api/audit/*", RoutePolicy.CacheFirst(TimeSpan.FromDays(7)) with { InvalidateCacheOnWrite = false })
```

> **`NetworkOnly` governs writes as well as reads.** It's the one strategy that does. An offline
> write to a `NetworkOnly` route is **not queued** — it goes to the transport and fails as it
> would without Hyperwyc installed. Use it where deferring a write is the wrong answer even
> though deferring a read would be fine: a payment, a seat reservation, anything contending for a
> shared mutable resource. Declining to take custody is more honest than a `202` Hyperwyc might
> honour hours later.

> **TTL says how old a cached response may be and still be served — nothing else.** It means the
> same thing online and offline. Past it, the response is not served: online it is refetched,
> offline the caller gets the same "no data" answer as if nothing were cached.
>
> So set it to how long the data is genuinely useful, not to how often you would like to
> refresh. "Always fetch when I can" is `NetworkFirst`, which is a strategy — using a short TTL
> to force refetching would leave you nothing to serve offline, which is the opposite of the
> point. The default is one day.

`NetworkOnly` opts out of the store entirely, so it has nothing to offer offline.

The app doesn't need to know the difference. Your existing code doesn't change.

> **Why "no data" instead of "no connection"?** Connectivity is an infrastructure concern, not an application one. Your code already has to handle the empty-result path (a search with no matches, a feed with no items); offline simply produces the same shape. A caller that does want to know reads the [`X-Hyperwyc-Status` header](responses.md) — or, for a write, the `202`, which no ordinary success is.

## Designing your responses

When Hyperwyc has nothing to give you — an offline read with no cached copy, or a write it has
only queued — it returns the JSON `null` literal, not an empty body. That distinction matters
more than it looks:

```csharp
var product  = await http.GetFromJsonAsync<Product>("/products/1");        // null
var products = await http.GetFromJsonAsync<List<Product>>("/products");    // null
```

Both return `null` rather than throwing. An *empty* body would throw `JsonException` from inside
the extension method — for a single object every bit as much as for a collection — because an
empty body is not "no data", it is not JSON at all.

So the case you need to handle is the one you already handle: a `null` result.

```csharp
var products = await http.GetFromJsonAsync<List<Product>>("/products");

if (products is null)
{
    // Offline with nothing cached. Show an empty state, or check
    // X-Hyperwyc-Status if you want to say why.
    return [];
}
```

**A collection comes back as `null`, not empty.** Returning `[]` would need Hyperwyc to know the
route returns a collection, which is knowledge it does not have. A null-coalesce at the call
site covers it — and you need one for the online path anyway, since a server returning an empty
response or a `204` produces the same shape.

If you would rather branch on status codes than on `null`, read
[`X-Hyperwyc-Status`](responses.md) to find out. And if your API
already uses an envelope or result type — [`Ardalis.Result`](https://github.com/ardalis/Result)
or a hand-rolled `ApiResponse<T>` — that keeps working, since the envelope simply deserialises
to `null` and your existing handling takes over.
