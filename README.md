# Hyperwyc

**A Service Worker for .NET.** An `HttpClient` handler that caches responses, queues writes made
offline, and replays them when the network comes back — without your calling code changing.

```bash
dotnet add package Hyperwyc
```

```csharp
services.AddSingleton<IConnectivityService, MyConnectivityService>();

services.AddHttpClient("MyApi")
    .AddHyperwycHandler()
    .AddHttpMessageHandler<AuthHandler>();

services.AddHyperwyc();
```

That's the setup. Your existing calls are untouched:

```csharp
// Online: fetched, and cached on the way back.
// Offline: served from cache. Same code, same shape, no branch.
var products = await client.GetFromJsonAsync<List<Product>>("/products");

// Online: sent. Offline: queued, and you get 202 Accepted.
// Either way it reaches the server; you find out which from the status code.
var response = await client.PostAsJsonAsync("/sales", sale);
```

| | |
|---|---|
| **Offline reads** | Served from cache, with a per-route TTL saying how old is too old |
| **Offline writes** | Queued durably and replayed on reconnect, through your own pipeline so auth still applies |
| **Outcomes** | An event stream tells you what the server eventually said about a deferred write |
| **Storage** | Durable and encrypted out of the box — no store to choose, nothing to wire up |

## Is it right for your app?

The useful question isn't whether you need to work offline — it's **who else writes to the same
record**.

- **Append-only, one writer per record** — an inspection report, a social post, a timesheet entry.
  Good fit. Nothing to resolve: queue it, replay it, done.
- **A shared mutable resource** — stock levels, seat reservations, a balance. Poor fit. An offline
  write is conflict-prone by construction, and no transport-layer tool can help, because the
  conflict is real.

Hyperwyc is a transport-layer component, not a local database. If your state needs to be queried
offline, it needs its own store with Hyperwyc delivering alongside it.

## Documentation

**[Full documentation →](docs/)**

[Is Hyperwyc right for your app?](docs/choosing.md) ·
[Getting started](docs/getting-started.md) ·
[Connectivity](docs/connectivity.md) ·
[Caching and route policies](docs/caching.md) ·
[Offline writes](docs/offline-writes.md) ·
[Events](docs/events.md) ·
[Pipeline placement](docs/pipeline.md) ·
[Storage](docs/storage.md)

[Architecture decisions](docs/decisions/) — why the scope is what it is, and why some obvious
features are deliberately absent.

## Packages

| Package | Use it when |
|---|---|
| `Hyperwyc` | Almost always. Includes durable [Cabinet](https://github.com/mattgoldman/cabinet)-backed storage |
| `Hyperwyc.Core` | You are supplying your own `IHyperwycStore`. No storage dependency |

## Licence

[MIT](LICENSE).
