# Is Hyperwyc right for your app?

Hyperwyc is narrow on purpose. This is what it is for, what it is not for, and how to tell which
side your application falls on.

## When to use it

- You want a **service-worker-like** drop-in resilience layer for .NET HTTP clients
- You need offline resilience without rewriting your app around a sync framework
- You want API calls to look and feel the same online or offline
- You want transport-level durability, not a storage-first sync engine
- Your app already has a stable API contract and you don't want to rearchitect

## What it doesn't do

- Doesn't handle auth or token refresh (your own handler should — place it after `HyperwycHandler`)
- Doesn't dictate your data model
- Doesn't replace your local database
- Doesn't resolve data conflicts — it's designed for scenarios where conflicts are rare or handled server-side

## Is your app a good fit?

The useful question isn't "does it need to work offline" — it's **who else writes to the same
record**.

**Append-only, one writer per record** — a social post, an inspection report, a timesheet entry.
Good fit. Nobody else is editing your record, so there's nothing to resolve: queue it, replay it,
done.

**A shared mutable resource** — stock levels, seat reservations, an account balance. Poor fit.
Many actors mutate one value, so an offline write is conflict-prone by construction and rejection
on replay is the normal case rather than an edge case. No transport-layer tool can help with that,
because the conflict is real. You want conflict resolution at the origin — event sourcing, or
whatever your domain calls for — and Hyperwyc has no opinion about it.

Hyperwyc is a **transport-layer tool**. If your application's state needs to survive and be
queried offline, it needs its own store, with Hyperwyc delivering alongside it rather than
instead of it.

---

## Current limitations

- **Buffered bodies only.** Request and response bodies are read into memory in full before being queued or cached. Binary payloads round-trip byte for byte — file uploads, image downloads, protobuf, gzip — but streaming uploads and downloads of indeterminate length are not supported.

---

## Hyperwyc is not a local database

It is a transport-layer component. It caches HTTP responses and delivers HTTP requests that could
not go out at the time; it does not hold your application's state.

If your application needs its own data to survive and be **queried** offline — a list the user
scrolls, a record they edit, anything you read back by something other than the URL that produced
it — then it needs its own store, and Hyperwyc sits alongside that store as the delivery
mechanism rather than replacing it. The usual shape is:

1. Write to your own database, and render the UI from it.
2. POST to your API through a Hyperwyc-handled client.
3. Mark the record unsynced from the `202`, and mark it synced when the matching
   [event](events.md) arrives.

That is a small amount of glue, and it is the difference between an application that works
offline and one that merely does not crash.

## How it compares

### CommunityToolkit.Datasync

- **Philosophy:** Entity-level synchronisation between client and server tables.
- **Server coupling:** Requires an ASP.NET Core backend with Datasync server components.
- **Domain model:** Mirrors database entities to the client; assumes close schema alignment
  between client and API.
- **Offline model:** Synchronises entire table sets; the API surface must match the data model.
- **Drawback:** Requires rearchitecting around the sync engine; applications must shape their
  domain model to fit Datasync's expectations.

### Realm

- **Philosophy:** Persistent object graph synchronised with a MongoDB Atlas backend.
- **Server coupling:** Requires MongoDB Realm backend services (now EOLed in favour of Atlas SDKs).
- **Domain model:** Heavily coupled to the Realm storage format and object model.
- **Drawback:** Tight backend lock-in and schema mirroring; unsuitable for REST- or
  GraphQL-based APIs.

### Hyperwyc

- **Philosophy:** Service-worker-inspired HTTP handler — transparent request/response caching
  and replay at the transport layer. The caller receives normal-looking responses regardless
  of connectivity state.
- **Server coupling:** None — works with any HTTP backend.
- **Domain model:** Fully independent; no schema mirroring, no requirement to align API
  surface with storage.
- **Integration:** Drop-in `DelegatingHandler`; can be added to any existing app without
  restructuring.
- **Use case fit:** Ideal for apps where API contracts are already stable, or where data
  conflicts are rare or handled server-side.

**TODO:** Evaluate language used; we're using "sync" a lot in the code and it conflates Hyperwyc with these other solutions. May be fine, but needs an active decision rather than it just falling out of what we did.

In essence, Datasync and Realm require you to architect your app *around* their sync model.
Hyperwyc fits *into* your existing architecture — like adding a Service Worker to a web app:
invisible by default, powerful when you need it.

---
