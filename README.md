# Hyperwyc

**A Service Worker for .NET.**
    
An `HttpClient` handler that caches responses, queues writes made offline, and replays them when the network comes back, *without changing your calling code.*

## Quick start

Install the package:

```bash
dotnet add package Hyperwyc
```

Register the Hyperwyc handler with your `HttpClient`, and register Hyperwyc in DI:

```csharp
services.AddHttpClient("MyApi")
    .AddHyperwycHandler()
    .AddHttpMessageHandler<AuthHandler>();

services.AddHyperwyc();
```

That's all you need to get started, and your existing calls are untouched:

```csharp
// Online: fetched, and cached on the way back
// Offline: served from cache, from the same code
var products = await client.GetFromJsonAsync<List<Product>>("/products");

// Online: sent normally
// Offline: queued, and you get 202 Accepted
var response = await client.PostAsJsonAsync("/sales", sale);
```

Hyperwyc is invisible to your `HttpClient` caller code and processes requests whether you are online or not. Hyperwyc's default behavior:

| Connectivity       | Behaviour                                                                                                            |
| ------------------ | -------------------------------------------------------------------------------------------------------------------- |
| **Offline reads**  | Served from cache, with a customisable per-route TTL                                                                 |
| **Offline writes** | Queued durably and replayed on reconnect, through your own pipeline (i.e. auth or any other handlers are re-applied) |
| **Outcomes**       | Hyperwyc emits an event stream, so you can still find out what happened to a deferred write when it eventually sent  |
| **Storage**        | Durable and encrypted out of the box, with BYO optional                                                              |

## Is it right for your app?

Use Hyperwyc if you want to queue reads and writes to an API while offline, without re-architecting your solution around a sync engine. Or if the idea of "synchronising" doesn't sit right with you at all (see [Choosing](docs/choosing.md) for more on this).

If you need to query state offline, it needs its own store. Hyperwyc can still be used to provide durable offline delivery, but think of it as a transport-layer component, not a local database.

You can (and, arguably, should) consider using Hyperwyc in any app, especially mobile and other UI apps, potentially IoT, or any scenario where you want API calling code to work offline.

It's still not necessarily the right approach for everything, even in apps where it is worthwhile including.

As a general rule of thumb:

- **Append-only, one writer per record**: e.g. an inspection report, a social post, a timesheet entry.
  Hyperwyc is perfect for these scenarios. As there is nothing to resolve, you can comfortably queue the request, and replay it, done.
- **A shared mutable resource**: e.g. stock levels, seat reservations, a balance.
  These may be a poor fit for offline write caching, use per-route policies to exclude them if necessary (see [Caching and delivery](docs/delivery.md))

## Documentation

**[Full documentation →](docs/)**

[Is Hyperwyc right for your app?](docs/choosing.md) ·
[Getting started](docs/getting-started.md) ·
[Connectivity](docs/connectivity.md) ·
[Caching and route policies](docs/delivery.md) ·
[Offline writes](docs/offline-writes.md) ·
[Events](docs/events.md) ·
[Pipeline placement](docs/pipeline.md) ·
[Storage](docs/storage.md)

[Architecture decisions](docs/decisions/): why the scope is what it is, and why some seemingly obvious features are deliberately absent.

## Packages

| Package         | Use it when                                                                                      |
| --------------- | ------------------------------------------------------------------------------------------------ |
| `Hyperwyc`      | Almost always. Includes durable [Cabinet](https://github.com/mattgoldman/cabinet)-backed storage |
| `Hyperwyc.Core` | You are supplying your own `IHyperwycStore`. No storage dependency                               |

## Licence

[MIT](LICENSE).

## AI Usage Disclosure Statement

AI was used heavily to build Hyperwyc. I produced the entirety of the architecture, technical decisions, and solution design, while relying on AI to generate most of the code. AI was used extensively for "rubber ducking", assistance with some blockers, all of the tests, most documentation, and recording decisions along the way.

I assume sole responsibility for the code contained in this library; its authorship and design are mine regardless of the tools using to generate it. Using AI does not absolve us of responsibility for the work we use it for.

Equally, responsibility for usage of Hyperwyc in your own products, including its fit for your purpose, rests with you. Hyperwyc is provided under the terms of a the LICENSE file, including warranty disclaimer and limitation of liability. We are all responsible for our own choices.