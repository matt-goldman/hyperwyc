# 5. Varying it per route

*[Tutorial index](README.md) · [← 4. Telling Hyperwyc about your network](4-connectivity.md)*

Everything so far used one policy for every route: serve a stored response if it is less than a day old, otherwise fetch; queue writes that cannot be sent. That is the default, and it is a reasonable one, but it is not right for everything.

## Set the default, then carve out exceptions

```csharp
services.AddHyperwyc(options =>
{
    options.Routes
        .For("/*",          RoutePolicy.CacheFirst(TimeSpan.FromHours(1)))
        .For("/sales/*",    RoutePolicy.NetworkFirst(TimeSpan.FromSeconds(30)))
        .For("/payments/*", RoutePolicy.NetworkOnly());
});
```

Written out, that forms a pyramid: the broadest rule at the top, each line narrowing the scope (the length of the route is inversely proportional to the specificity). It is the `.gitignore` and CSS-cascade model — **state the general rule, then carve out the exceptions** — so where two patterns both match, **the one registered later wins**.

Patterns match on the URL *path* only, so they work whatever your `BaseAddress` is.

## The three strategies

- **`CacheFirst`** serves a stored response without touching the network, if it is within its TTL. The catalogue on page 1.
- **`NetworkFirst`** always tries the network and falls back to the store only when that fails. For data you want fresh but can live without.
- **`NetworkOnly`** never touches the store in either direction — and it is the one strategy that governs *writes* too. A write to such a route is never queued.

## TTL is a validity bound, not a refresh timer

This one catches people, so it is worth being precise.

TTL says **how old a stored response may be and still be served**. Past it, the response is not served at all: online it is refetched, offline the caller gets the same "no data" answer as if nothing had ever been stored.

It means the same thing whether or not there is a network. So set it to how long the data is genuinely useful — not to how often you would like to refresh. "Fetch fresh whenever I can" is `NetworkFirst`, which is a strategy; a short TTL used to force refetching just leaves you nothing to serve offline, which is the opposite of the point.

## Try it

Give the catalogue a ten-second TTL, fetch it, stop the API, and fetch it twice:

```csharp
options.Routes.For("/products", RoutePolicy.CacheFirst(TimeSpan.FromSeconds(10)));
```

Immediately after stopping the API you get the two products. Wait eleven seconds and you get `null` with `X-Hyperwyc-Status: Offline` — the stored copy is still on disk, but by the policy you wrote it is no longer fit to serve, and Hyperwyc will not hand you data you said was too old.

## That's the tour

You have seen every moving part:

|                        |                                                         |
| ---------------------- | ------------------------------------------------------- |
| Reads survive          | served from the store when the API cannot be reached    |
| Writes survive         | queued durably, answered `202`, replayed later          |
| Outcomes come back     | on the event stream, correlated to an id you chose      |
| Connectivity is a hint | the transport is what actually knows                    |
| Policy is per route    | strategy, TTL, and the routes that must not be deferred |

## Where to go next

- **[Hyperwyc in a .NET MAUI app](../maui.md)** — if that is what you are building, this is the one page that matters.
- **[Design](../design.md)** — why the `202`, why a `200` and not a `404`, why there is no retry, and what Hyperwyc is quietly asking you to believe about your own architecture.
- **[Reference](../README.md#reference)** — the exact behaviour of each part, in tables.
- **[Architecture decisions](../decisions/)** — why some obvious feature is absent. The answer is usually there, and usually deliberate.
