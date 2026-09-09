# Is Hyperwyc right for your app?

This page explains what Hyperwyc is and isn't and aims to provide guidance on when to use it, when to control _how_ you use it with per-route policies, and, occasionally, when not to use it at all.
    
Hyperwyc is deliberately focused on as narrow a goal as possible. It is not a local or offline database or datastore, and it is not a synchronisation engine (i.e. it has no conflict resolution logic). The full reasoning and scope decision is covered in the [design](design.md) document.

## When to use it

- You want a **service-worker-like** drop-in resilience layer for .NET HTTP clients (see [this Digi Invent article](https://digiinvent.com/service-worker/) or [the official spec](https://developer.mozilla.org/docs/Web/API/Service_Worker_API)) (**note**: Hyperwyc does not handle push notifications)
- You need offline resilience without rewriting your app around a sync framework
- You want API calls to look and feel the same online or offline
- You want transport-level durability, not a storage-first sync engine
- Your app already has a stable API contract and you don't want to rearchitect

## What it doesn't do

- Doesn't handle auth or token refresh (your own handler should; place it *after* `HyperwycHandler`)
- Doesn't dictate your data model
- Doesn't replace your local database
- Doesn't resolve data conflicts — it's designed for scenarios where conflicts are rare or unexpected, or handled server-side
- Doesn't handle push notifications

## Is your app a good fit?

If you want your app to work offline, Hyperwyc is almost certainly a great option.

It sits in your HTTP request pipeline, and can cache reads, serving them from the cache when offline, and queue writes (durably, not just in memory), and send them when a connection is detected, or on start (or whenever you want to trigger it).

Traditionally this is solved with either a synchronisation engine (see below) or custom logic in your local store that tracks send and receive state. Hyperwyc gives you offline read and write assurance without any of that.

With that said Hyperwyc is not a replacement for an offline store, or synchronisation between local and remote state (if that's what you need). For writes (e.g. `POST`, `PUT`, `PATCH`, `DELETE`), the scenarios to consider are:

* **Append-only, one writer per record** e.g. a social post, an inspection report, a timesheet entry. Hyperwyc is a great fit for this. Nobody else is editing your record, so a write that arrives late cannot conflict with anything: queue it, replay it, done.

* **A shared mutable resource** e.g. stock levels, seat reservations, an account balance. If you rely solely on Hyperwyc for this, you will face problems. Many actors mutate one value, so an offline write is conflict-prone and rejection on replay is the normal case rather than an edge case. No transport-layer tool can help with that, because the conflict is real. You want conflict resolution at the origin (e.g. event sourcing, or whatever your domain calls for) and Hyperwyc has no opinion about it.
    
For reads (e.g. `GET`) there is rarely a reason not to use it; you just have to think about your policies and pick a sensible TTL.

## Current limitations

- **Buffered bodies only.** Request and response bodies are read into memory in full before being queued or cached. Binary payloads round-trip byte for byte (file uploads, image downloads, protobuf, gzip), but *streaming uploads and downloads of indeterminate length* are not currently supported.
- **`Cache-Control` is not honoured**, including `no-store`. A response the server asked you not to store is stored. Use a `NetworkOnly` route policy for anything that matters.
- **The cache is not bounded.** Individual response bodies are capped at 512 KB, but the store as a whole grows without limit and nothing evicts. On a long-lived mobile app that is the number to watch, and on Android it is what runs into the 25 MB backup quota — see [Storage](storage.md).
- **`Vary` is not honoured.** The cache is keyed on URL alone, so a content-negotiated endpoint can serve the wrong variant.
- **Not AOT-safe.** There is no `JsonSerializerContext`, so the store falls back to reflection-based serialisation. This matters most on iOS, where release builds have AOT on by default.

These are known and tracked, not surprises; they are the reason Hyperwyc is pre-1.0.

## Hyperwyc is not a local database

It is a transport-layer component. It caches HTTP responses and delivers HTTP requests that could not go out at the time; it does not hold your application's state.

If your application needs its own data to survive and be **queried** offline — a list the user scrolls, a record they edit, anything you read back by something *other* than the URL that produced it — then it needs its own store, and Hyperwyc sits alongside that store as the delivery mechanism rather than replacing it. The usual shape is:

1. Write to your own file or database, and render the UI from it
2. `POST` to your API through a Hyperwyc-handled client
3. Mark the record unsynced from the `202` that Hyperwyc returns, and mark it synced when the matching [event](events.md) arrives

That is a small amount of glue, and the difference between an application that works offline and one that merely does not crash.

## How it compares

Hyperwyc was built because existing solutions followed a similar pattern and shared some limitations. The common theme is that they couple both your architecture and your back end data to a single sync requirement. That is an opinion about something beyond the reach of one problem, imposed on things outside its own scope, but worse, for existing solutions adoption requires massive amounts of rework - if you already have a full set of API routes/endpoints, and a fully working solution, you cannot simply drop these in, you _must_ redesign client connectivity from the ground up.

Hyperwyc is built on the belief that you should be able to drop something into your pipeline that handles the majority of scenarios without dictating infrastructure or entity design. Web has had this for over a decade, and now .NET does too.

This section is a brief comparison with leading alternatives, with a summary of the gap Hyperwyc fills.

### CommunityToolkit.Datasync

This is a community replacement for Azure Mobile Apps, which is a deprecated service. It follows the same principles and design.

- **Philosophy:** Entity-level synchronisation between client and server tables.
- **Server coupling:** Requires an ASP.NET Core backend with Datasync server components.
- **Domain model:** Mirrors database entities to the client; assumes close schema alignment between client and API.
- **Offline model:** Synchronises entire table sets; the API surface must match the data model.
- **Drawback:** Requires rearchitecting around the sync engine; applications must shape their domain model to fit Datasync's expectations.

### Realm

Realm was a popular choice provided by MongoDB. It worked well for a long time, but the SDKs were deprecated in September 2024. The functionality is still available, but requires a specific cloud service, rather than a feature in any MongoDB instance. Either way, it dictates your application model beyond the scope of offline functionality for clients.

- **Philosophy:** Persistent object graph synchronised with a MongoDB Atlas backend.
- **Server coupling:** Requires MongoDB Realm backend services (now EOLed in favour of Atlas SDKs).
- **Domain model:** Heavily coupled to the Realm storage format and object model.
- **Drawback:** Tight backend lock-in and schema mirroring; unsuitable for REST- or GraphQL-based APIs.

### Hyperwyc

- **Philosophy:** Service-worker-inspired HTTP handler: transparent request/response caching and replay at the transport layer. The caller receives normal-looking responses regardless of connectivity state.
- **Server coupling:** None. Works with any HTTP backend.
- **Domain model:** Fully independent; no schema mirroring, no requirement to align API surface with storage.
- **Integration:** Drop-in `DelegatingHandler`; can be added to any existing app without restructuring.
- **Use case fit:** Ideal for apps where API contracts are already stable, or where data conflicts are rare or handled server-side.

In essence, Datasync and Realm require you to architect your app *around* their sync model. Hyperwyc fits *into* your existing architecture, like adding a Service Worker to a web app: invisible by default, powerful when you need it.

## Not alternatives

Datasync and Realm are things you would reach for *instead of* Hyperwyc. Two others come up often and are not in that category at all: one sits alongside it, and the other is what you would be taking on yourself.

### A resilience handler

[Polly](https://github.com/App-vNext/Polly), or `Microsoft.Extensions.Http.Resilience`, is not an alternative to Hyperwyc. It is the other half, and the two answer different questions.

A resilience handler retries **within one attempt**, on a schedule that tracks the failure it just saw — backoff, jitter, `Retry-After`, a circuit breaker. Hyperwyc decides whether there should be **another attempt at all**, possibly hours later and across a process restart.

The dividing line is durability. Polly's retries live in memory: kill the app and they are gone with it. Hyperwyc's queue is on disk and survives being killed, which on a phone is not an edge case — Android and iOS terminate suspended processes without running disposal or anything you registered. So a resilience handler covers "the server is briefly unhappy"; Hyperwyc covers "there is no network, and there may not be one for a while".

They compose rather than compound. Register the resilience handler *after* `AddHyperwycHandler()` and it retries inside a single attempt while Hyperwyc decides whether to make another — and when Hyperwyc answers from its own store, the resilience handler is never invoked at all, so an offline read costs no backoff. See [Pipeline placement](pipeline.md).

This is also why Hyperwyc does not retry a request the server answered, whatever it said. Not an omission: that job already has an owner, and doing it twice would be worse than doing it once. See [ADR 0001](decisions/0001-idempotency-is-not-hyperwycs-remit.md).

### Writing your own `DelegatingHandler`

You could, and this repo is the proof. If you only need the read half, caching a response and serving it back is something you could build in an afternoon, and a hand-rolled version you understand beats a dependency you do not need.

The write path is where it gets complicated. What has to be decided, roughly in the order it bites:

- **Surviving process death.** The outbox has to be on disk *before* you answer the caller, because on mobile there is no shutdown hook to flush it in. That means a store, a serialisation format, and a shape you can change later without stranding what is already written.
- **Replaying through the pipeline.** A write queued on Monday and sent on Tuesday needs Tuesday's token. That means putting the replay back through the client's own handler chain rather than a bare transport, without the handler catching it a second time and queueing it again.
- **Knowing which failures are safe to replay.** Only four `HttpRequestError` values mean nothing was transmitted. The others may have reached your API, and replaying those duplicates a write on a guess.
- **What to do when the store itself cannot be read** — a rotated key, a moved directory, a file that no longer deserialises. Failing loudly loses the app; failing silently loses the writes.
- **Not throwing where the caller does not expect it**, which turns out to be about response bodies rather than status codes.

It's a series of decisions, and the trap is that most of them have a wrong answer that looks correct until you stress test it. That is what [the decision records](decisions/) are, and a fair number of them were written after getting the first answer wrong.

But with all that said, none of that is insurmountable, and none of it is really difficulty, so if you want to write your own, and use this as inspiration, that's a valid option (if you have the time). The decisions recorded here (in the code and the decision record) have probably solved most of the hard problems, and with any luck, that can benefit you whether consuming the package or writing your own version.
