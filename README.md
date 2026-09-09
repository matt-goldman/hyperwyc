# Hyperwyc

**HTTP When You Can: A Service Worker for .NET.**
    
An `HttpClient` handler that caches responses, queues writes it cannot send, and replays them when the network comes back, *without changing your calling code.*

*(**Hyper**text transfer protocol, **w**hen **y**ou **c**an.)*

## Quick start

Your API is unreachable — a dropped connection, a phone in a lift, a server that went away. Without Hyperwyc:

```csharp
var products = await client.GetFromJsonAsync<List<Product>>("/products");

// Unhandled exception. System.Net.Http.HttpRequestException: Connection refused
```

Install it, and add two registrations:

```bash
dotnet add package Hyperwyc
```

```diff
  services.AddHttpClient("MyApi")
+     .AddHyperwycHandler()
      .AddHttpMessageHandler<AuthHandler>();   // yours, if you have one
+
+ services.AddHyperwyc();
```

Run the same line again, with the API still unreachable:

```csharp
var products = await client.GetFromJsonAsync<List<Product>>("/products");

// 2 products, first = Anvil
```

Served from a durable, encrypted store that was filled the last time the request succeeded — and **the call site never changed**. A write goes the same way: rather than throwing, it is kept and replayed when the network returns.

```csharp
var response = await client.PostAsJsonAsync("/sales", sale);

// 202 Accepted, X-Hyperwyc-Status: Queued
```

Register `AddHyperwycHandler()` first, before your own handlers. Everything after it also runs on replayed writes, which is how a write queued on Monday goes out with Tuesday's token.

That is the whole setup. Hyperwyc's default behaviour:

| Connectivity       | Behaviour                                                                                                            |
| ------------------ | -------------------------------------------------------------------------------------------------------------------- |
| **Offline reads**  | Served from cache, with a customisable per-route TTL                                                                 |
| **Offline writes** | Queued durably and replayed on reconnect, through your own pipeline (i.e. auth or any other handlers are re-applied) |
| **Outcomes**       | Hyperwyc emits an event stream, so you can still find out what happened to a deferred write when it eventually sent  |
| **Storage**        | Durable and encrypted out of the box, with BYO optional                                                              |

[Walk through it properly →](docs/tutorial/) — five short pages, on a desktop, with an API you stop yourself.

## Is it right for your app?

Use Hyperwyc if you want reads served and writes queued while offline, without re-architecting your solution around a sync engine. Or if the idea of "synchronising" doesn't sit right with you at all (see [Choosing](docs/choosing.md) for more on this).

If you need to query state offline, it needs its own store. Hyperwyc can still be used to provide durable offline delivery, but think of it as a transport-layer component, not a local database.

You can (and, arguably, should) consider using Hyperwyc in any app, especially mobile and other UI apps, potentially IoT, or any scenario where you want API calling code to work offline.

It's still not necessarily the right approach for everything, even in apps where it is worthwhile including.

**Reads are the easy half.** Serving a stored response when the network is gone costs nothing, and only risks staleness, and a TTL is how you bound that. There is rarely a reason not to.

**Writes are worth a thought, route by route.** A queued write is delivered later, so the question is whether your app can tolerate a wait, or whether it needs to know immediately when delivery has failed (and respond accordingly). Data that only your own user writes, e.g. an inspection report, a timesheet entry, etc., are usually fine: queue it, replay it when you can, done. Where many actors change one value, like stock levels or a seat reservation, essentially any scenario where not immediately reporting that the payload could not be delivered, a transport-layer tool can't help, and in fact makes it worse. Those are the routes to exclude with a [per-route policy](docs/delivery.md).

[Is Hyperwyc right for your app?](docs/choosing.md) has the longer version.

## Documentation

**[Full documentation →](docs/)**

|                                                     |                                                                    |
| --------------------------------------------------- | ------------------------------------------------------------------ |
| [Is Hyperwyc right for your app?](docs/choosing.md) | You are evaluating                                                 |
| [Getting started](docs/getting-started.md)          | Install, register, make a request                                  |
| [Hyperwyc in a .NET MAUI app](docs/maui.md)         | Everything a MAUI app needs, on one page                           |
| [Tutorial](docs/tutorial/)                          | Five short pages, building an app that survives its API going away |
| [Design](docs/design.md)                            | Why it behaves the way it does, and the opinions it holds          |

Reference: [delivery and route policies](docs/delivery.md) ·
[offline writes](docs/offline-writes.md) ·
[synthetic responses](docs/responses.md) ·
[events](docs/events.md) ·
[connectivity](docs/connectivity.md) ·
[pipeline placement](docs/pipeline.md) ·
[storage](docs/storage.md) ·
[testing](docs/testing.md)

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

I assume sole responsibility for the code contained in this library; its authorship and design are mine regardless of the tools used to generate it. Using AI does not absolve us of responsibility for the work we use it for.

Equally, responsibility for usage of Hyperwyc in your own products, including its fit for your purpose, rests with you. Hyperwyc is provided under the terms of the LICENSE file, including warranty disclaimer and limitation of liability. We are all responsible for our own choices.

