# Hyperwyc documentation

A Service Worker for .NET: an `HttpClient` handler that caches responses, queues writes made
offline, and replays them when the network returns. Your calling code does not change.

## Start here

| | |
|---|---|
| **[Is Hyperwyc right for your app?](choosing.md)** | What it is for, what it is not for, and how it compares to Realm and CommunityToolkit.Datasync. Read this first if you are evaluating |
| **[Getting started](getting-started.md)** | Install, register, make a request |
| **[Connectivity](connectivity.md)** | What the default does, which way it errs and why that is the safe way, and when to replace it |

[comment: Three entries under "Start here" and six under "Using it", all six of which are reference documents. If the tutorial lands, this table becomes the shape of the whole restructure: Choosing (evaluate) -> Tutorial (learn) -> Quick starts (MAUI, host apps) -> Reference -> Why it works this way. That reordering is the progressive disclosure you are after and it can be done here before a single page is rewritten.]

## Using it

| | |
|---|---|
| **[Caching and route policies](delivery.md)** | Where a response comes from, how long it stays usable, and how to vary both per route |
| **[Offline writes](offline-writes.md)** | Queueing, replay, and what happens when a write fails |
| **[Synthetic responses](responses.md)** | The status codes and headers Hyperwyc returns when it answers instead of the server |
| **[Events](events.md)** | Finding out what the server eventually said about a deferred write |
| **[Pipeline placement](pipeline.md)** | Where the handler sits, and what that means for auth |
| **[Storage](storage.md)** | Location, encryption at rest, and what happens when the store cannot be read |

## Why it is this way

**[Architecture decisions](decisions/)** — the reasoning behind Hyperwyc's scope, kept so a
future decision of the same shape can be answered consistently rather than re-argued. Worth
reading if you are wondering why some obvious feature is absent; the answer is usually there, and
usually deliberate.
