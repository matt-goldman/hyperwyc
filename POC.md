# Hyperwyc — Sample Application

## Purpose

Demonstrate Hyperwyc in a realistic setting: an offline-capable .NET MAUI app talking to an
ASP.NET Core API, with all HTTP traffic running through `HyperwycHandler`. The app makes
ordinary `HttpClient` calls and gets ordinary-looking responses whether or not the device has
a network — which is the whole claim, so the sample exists to make it visible.

The domain is a **field sales app**: a rep browses a product catalogue and records sales, often
somewhere with no signal.

That scenario is chosen deliberately. Reads and writes hit *different* resources, so the two
halves of Hyperwyc can be demonstrated independently — a catalogue that must remain readable
offline, and sales that must not be lost when there is nowhere to send them.

---

## Solution Structure

An [Aspire](https://learn.microsoft.com/dotnet/aspire/) solution under `sample/`:

```
sample/
├── Hyperwyc.Sample.AppHost/            # Aspire orchestration — the thing you run
├── Hyperwyc.Sample.ApiService/         # ASP.NET Core API (all in Program.cs)
├── Hyperwyc.Sample.Maui/               # .NET MAUI client
├── Hyperwyc.Sample.ServiceDefaults/    # Aspire defaults for the API
├── Hyperwyc.Sample.MauiServiceDefaults/# Aspire defaults for the MAUI app
└── Shared/                             # Product, Sale
```

---

## Sample API

Minimal, in-memory, entirely in `Program.cs`. No database, no migrations, nothing to set up.

| Method | Route | Description |
|--------|-------|-------------|
| `GET` | `/products` | The catalogue |
| `GET` | `/products/{id}` | A single product |
| `POST` | `/products/regenerate` | Build a brand new catalogue without restarting |
| `GET` | `/sales` | Sales recorded so far |
| `POST` | `/sales` | Record a sale; decrements stock |

### The catalogue is randomly generated

Names are assembled from components (`Small-batch Copper Planter`, `Rustic Bamboo Stool`), with
random prices and stock levels, and 8–16 products per catalogue. It is regenerated on every API
start, and on demand via `POST /products/regenerate`.

This is what makes cache staleness testable. A client holding a cached catalogue keeps showing
the old one — old names, old prices, old stock — until its TTL expires or something invalidates
it. Regenerating lets you provoke that without stopping anything.

It is generated rather than seeded from a real products API on purpose: a sample about working
without a network should not need one to start.

### Sales decrement stock

This is what makes staleness *consequential* rather than cosmetic. Record a sale and the cached
catalogue is now provably wrong about that product's stock.

It also gives deliberate ways to make a write fail, so the retry and dead-letter paths can be
demonstrated on demand:

| Status | Cause |
|--------|-------|
| `400` | Quantity of zero or less |
| `404` | Unknown product id |
| `409` | Insufficient stock — the easiest one to trigger, just oversell |

### Idempotency

`POST /sales` honours the `Idempotency-Key` header that Hyperwyc injects on every mutating
request. A repeated key returns the sale that key already produced, with `200 OK` rather than
`201 Created`, and does not decrement stock a second time.

This is what makes an offline queue safe to flush more than once — a sale that reached the
server but whose response never made it back is not recorded twice on replay.

---

## What the MAUI app should demonstrate

**Features:**
- Browse the product catalogue
- Record a sale
- Per-sale sync state (synced / queued / failed)
- Live sync event log
- "Sync now" button — `IHyperwyc.FlushAsync()`
- "Clear data" button — `IHyperwyc.ResetStoreAsync()`

**Behaviours:**
- App code makes plain `HttpClient` calls, with no connectivity branching anywhere
- Sales recorded offline are queued transparently (`202 Accepted` + `X-Hyperwyc-Status: Queued`)
- The catalogue stays readable offline, served stale rather than empty
- Queued sales replay automatically when connectivity returns
- A sale that keeps failing surfaces as dead-lettered

---

## Sync Flow

```
[Record a sale]
      │
      ▼
HyperwycHandler (in the HttpClient pipeline)
      │
      ├── Online?  ──Yes──▶ Send to API ──▶ OnSynced
      │
      └── Offline? ──────▶ Persist to Cabinet store ──▶ OnQueued
                                   │
                          [App start, or connectivity restored]
                                   │
                                   ▼
                   Replay through the same client pipeline,
                   with HyperwycHandler stepping aside
                                   │
                          ┌────────┴────────┴────────┐
                     Success        4xx            5xx
                        │            │              │
                    OnSynced   Dead-letter   Queued for a
                                    │        later attempt
                                 OnFailed         │
                                           (until the budget
                                            runs out, then
                                              OnFailed)
```

Note the replay path: a queued sale goes back through the *application's* pipeline, not around
it, so auth and any other handlers apply to it. `HyperwycHandler` recognises the replay and does
not intercept it a second time. See [Auth Handler Placement](README.md#auth-handler-placement).

---

## Running it

### Prerequisites

- .NET 10 SDK
- .NET MAUI workload (`dotnet workload install maui`)
- An Android emulator — the sample targets Android only, since it is the one target that runs
  from a Windows, macOS or Linux development machine

### Steps

```bash
cd sample
dotnet run --project Hyperwyc.Sample.AppHost
```

Aspire starts the API, provisions a dev tunnel so the emulator can reach it, launches the
Android emulator and deploys the app. The dashboard shows logs and traces for both.

To drive the API directly while the app is running:

```bash
curl http://localhost:<port>/products
curl -X POST http://localhost:<port>/products/regenerate
```

### Simulating offline

Use the emulator's network toggle, or enable aeroplane mode inside the emulator. Sales recorded
while offline queue locally and replay when connectivity returns.

### Things worth trying

| To see | Do this |
|---|---|
| Offline queueing | Go offline, record a sale, watch it appear as Queued |
| Automatic replay | Come back online and wait — no interaction needed |
| Manual replay | Come back online and press "Sync now" |
| Stale cache | `POST /products/regenerate`, then browse the catalogue in the app |
| Cache expiry | Regenerate, then wait out the TTL and refresh |
| Immediate failure | Oversell a product — the `409` dead-letters at once, since retrying cannot change it |
| Retry and dead-letter | Stop the API mid-sync — writes are retried across attempts, then dead-letter |
| Idempotent replay | Record a sale offline, come back online, confirm stock drops only once |

---

## Known rough edges

Worth knowing before you conclude something is broken:

- **A sale does not invalidate the cached catalogue.** Write-triggered invalidation matches on
  URL prefix, so `POST /sales` invalidates cached reads under `/sales`, not `/products` — even
  though the sale changed stock levels. Use a short TTL on the catalogue, an explicit refresh,
  or `SyncPolicy.ApiFirst()` for that route. Per-route policies
  ([issue 22](Backlog/22-v1-per-route-policies.md)) would let this be expressed properly.

---

## Future Sample Targets

| Platform | Status |
|----------|--------|
| .NET MAUI (Android) | ✅ This sample |
| .NET MAUI (iOS / macOS / Windows) | Should work; not part of the Aspire orchestration |
| Uno Platform / Avalonia / WinUI | Planned |
| Blazor (WASM + Server) | Planned — requires an IndexedDB store provider |
| .NET Nano / embedded | Exploratory |
