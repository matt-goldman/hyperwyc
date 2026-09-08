# Is Hyperwyc right for your app?

Hyperwyc is deliberately focused on as narrow a goal as possible. It is not a local or offline database or datastore, and it is not a synchronisation engine (i.e. it has no conflict resolution logic). As much as possible the reasoning behind this is captured in the [architecture decisions](decisions/README.md) (while some of it was captured after the fact the essence is all there). This document explains what Hyperwyc is and isn't and aims to provide guidance on when to use it, when to control _how_ you use it with per-route policies, and, occasionally, when not to use it at all.

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
- Notifications

[comment: Bare fragment in a list of "Doesn't ..." sentences. "Doesn't handle push notifications", which also matches the caveat at the top of the page.]

## Is your app a good fit?

If you want your app to work offline, Hyperwyc is almost certainly a great option.

It sits in your HTTP request pipeline, and can cache reads, serving them from the cache when offline, and queue writes (durably, not just in memory), and send them when a connection is detected, or on start (or whenever you want to trigger it).

Traditionally this is solved with either a syncrhonisation engine (see below) or custom logic in your local store that tracks send and receive state. Hyperwyc gives you offline read and write assurance without any of that.

[comment: "syncrhonisation" here and again on the next paragraph; "teh" further down in "How it compares".]

With that said Hyperwyc is not a replacement for an offline store, or syncrhonisation between local and remote state (if that's what you need). For writes (e.g. `POST`, `PUT`, `PATCH`, `DELETE`), the scenarios to consider are:

* **Append-only, one writer per record** e.g. a social post, an inspection report, a timesheet entry. Hyperwyc is a great fit for this. Nobody else is editing your record, so there's nothing to resolve: queue it, replay it, done.

* **A shared mutable resource** e.g. stock levels, seat reservations, an account balance. If you rely solely on Hyperwyc for this, you will face problems. Many actors mutate one value, so an offline write is conflict-prone and rejection on replay is the normal case rather than an edge case. No transport-layer tool can help with that, because the conflict is real. You want conflict resolution at the origin (e.g. event sourcing, or whatever your domain calls for) and Hyperwyc has no opinion about it.
    
For reads (e.g. `GET`), there's no reason I can think of *not* to use it, you just have to think carefully about your policies and pick a sensible TTL.

[comment: "there's no reason I can think of" is the only first-person singular in the docs. Deliberate or not, worth deciding - the rest of the voice is impersonal with an occasional "we".]

Hyperwyc is a **transport-layer tool**. If your application's state needs to queried offline, it needs its own store, with Hyperwyc delivering alongside it rather than instead of it.

[comment: This paragraph and the "Hyperwyc is not a local database" section below make the same argument at similar length, about 10 lines apart. One of them can go; the second is the better written of the two. ("needs to queried" - be.)]

## Current limitations

- **Buffered bodies only.** Request and response bodies are read into memory in full before being queued or cached. Binary payloads round-trip byte for byte (file uploads, image downloads, protobuf, gzip), but *streaming uploads and downloads of indeterminate length* are not currently supported.

[comment: This is the biggest accuracy gap in the docs. An evaluator reading a section called "Current limitations" reasonably concludes streaming bodies are the only one. The open backlog says otherwise, and at least four are consumer-visible and cannot be worked around:

  - Cache-Control is ignored entirely, including no-store, so a response the server said not to store is written to disk (item 41)
  - the cache grows without bound - individual bodies are capped, the store is not, and nothing evicts (item 42)
  - Vary is not honoured, so a content-negotiated endpoint serves the wrong variant, silently, and it looks like a server bug (item 43)
  - no JsonSerializerContext, so the store falls back to reflection - in a library whose primary audience ships iOS release builds with AOT on by default (item 53)

53 in particular belongs in front of anyone evaluating this for MAUI, and this is the page where they would look. These are open items rather than defects, which is precisely why they belong in the docs and not only in the backlog.]

## Hyperwyc is not a local database

It is a transport-layer component. It caches HTTP responses and delivers HTTP requests that could not go out at the time; it does not hold your application's state.

If your application needs its own data to survive and be **queried** offline — a list the user scrolls, a record they edit, anything you read back by something *other* than the URL that produced it — then it needs its own store, and Hyperwyc sits alongside that store as the delivery mechanism rather than replacing it. The usual shape is:

1. Write to your own file or database, and render the UI from it
2. `POST` to your API through a Hyperwyc-handled client
3. Mark the record unsynced from the `202`, and mark it synced when the matching [event](events.md) arrives

That is a small amount of glue, and the difference between an application that works offline and one that merely does not crash.

## How it compares

Hyperwyc was built because existing solutions followed a similar pattern and shared some limitations. The common theme is that they couple both your architecture and your back end data to a single sync requirement. That's opinion about something beyond teh reach of one problem that should not influence things outside its own scope, but worse, for existing solutions adoption requires massive amounts of rework - if you already have a full set of API routes/endpoints, and a fully working solution, you cannot simply drop these in, you _must_ redesign client connectivity from the ground up.

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

Realm was a popular choice provided by MongoDB. It worked well for a long time, but has been made end of life now. The functionality is still available, but requires a specific cloud service, rather than a feature in any MongoDB instance. Either way, it dictates your application model beyond the scope of offline functionality for clients.

- **Philosophy:** Persistent object graph synchronised with a MongoDB Atlas backend.
- **Server coupling:** Requires MongoDB Realm backend services (now EOLed in favour of Atlas SDKs).
- **Domain model:** Heavily coupled to the Realm storage format and object model.
- **Drawback:** Tight backend lock-in and schema mirroring; unsuitable for REST- or GraphQL-based APIs.

[comment: "has been made end of life now" above - worth dating it (SDK deprecation announced September 2024) so it ages honestly rather than becoming quietly wrong.]

### Hyperwyc

- **Philosophy:** Service-worker-inspired HTTP handler: transparent request/response caching and replay at the transport layer. The caller receives normal-looking responses regardless of connectivity state.
- **Server coupling:** None. Works with any HTTP backend.
- **Domain model:** Fully independent; no schema mirroring, no requirement to align API surface with storage.
- **Integration:** Drop-in `DelegatingHandler`; can be added to any existing app without restructuring.
- **Use case fit:** Ideal for apps where API contracts are already stable, or where data conflicts are rare or handled server-side.

In essence, Datasync and Realm require you to architect your app *around* their sync model. Hyperwyc fits *into* your existing architecture, like adding a Service Worker to a web app: invisible by default, powerful when you need it.

[comment: The comparison omits the two things a .NET reader most likely already has in mind: Microsoft.Extensions.Http.Resilience / Polly ("I already have retries") and "why not just write my own DelegatingHandler". Neither is a competitor exactly, which is the point - the answer to both is short, and it would land better here than anywhere else. Polly especially, given how much of the docs are about the boundary with it.]

