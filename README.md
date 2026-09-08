# Hyperwyc

**A Service Worker for .NET.**
    
An `HttpClient` handler that caches responses, queues writes made offline, and replays them when the network comes back, *without changing your calling code.*

[comment: "queues writes made offline" is now narrower than the behaviour. Writes are also queued when the device believes it is online and the transport cannot establish a connection - which is the headline of ADR 0007 and arguably the strongest single sentence you have. Consider "queues writes it cannot send".]

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

[comment: This table is headed "default behavior" (US) while its own first column reads "Behaviour" (UK) and the licence heading is "Licence". The repo is consistently British elsewhere - worth a pass.]

## Is it right for your app?

Use Hyperwyc if you want to queue reads and writes to an API while offline, without re-architecting your solution around a sync engine. Or if the idea of "synchronising" doesn't sit right with you at all (see [Choosing](docs/choosing.md) for more on this).

[comment: "queue reads and writes" - reads are not queued, they are cached and served. Small, but it is the second sentence an evaluator reads.]

If you need to query state offline, it needs its own store. Hyperwyc can still be used to provide durable offline delivery, but think of it as a transport-layer component, not a local database.

You can (and, arguably, should) consider using Hyperwyc in any app, especially mobile and other UI apps, potentially IoT, or any scenario where you want API calling code to work offline.

It's still not necessarily the right approach for everything, even in apps where it is worthwhile including.

As a general rule of thumb:

- **Append-only, one writer per record**: e.g. an inspection report, a social post, a timesheet entry.
  Hyperwyc is perfect for these scenarios. As there is nothing to resolve, you can comfortably queue the request, and replay it, done.
- **A shared mutable resource**: e.g. stock levels, seat reservations, a balance.
  These may be a poor fit for offline write caching, use per-route policies to exclude them if necessary (see [Caching and delivery](docs/delivery.md))

[comment: This append-only / shared-mutable split appears three times: here, in choosing.md's "Is your app a good fit?", and a third time in backlog item 50 which specifies it as the fit test. It is the best idea in the docs and repetition dilutes it. Suggest it lives once, in choosing.md, and the README carries two lines and a link.]

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

[comment: responses.md is missing from this list but present in docs/README.md. Also: this list calls delivery.md "Caching and route policies", the section above calls it "Caching and delivery", the file is delivery.md, and its H1 is "Delivery and Route Policies". Four names for one document. One name, used everywhere.]

[comment: Nothing here points a .NET MAUI reader anywhere specific, and connectivity.md - the page they most need - is 311 lines with the MAUI answer 80 lines in. This is the case for the MAUI quick start you flagged.]

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

[comment: Two typos in this section: "regardless of the tools using to generate it" (used), and "under the terms of a the LICENSE file".]