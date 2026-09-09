# Hyperwyc — proof of concept

## What this is

A proof of concept rather than a demo. It exists to prove Hyperwyc works on a real device —
offline reads across an app restart, offline writes replayed on reconnect, binary bodies — and it
has done that.

It is **not** a showcase, and the domain is a poor one for the library: stock level is a shared
mutable resource, so an offline sale is conflict-prone by construction and the `409` you will see
is routine rather than illustrative. See
[Is Hyperwyc right for your app?](../docs/choosing.md) for why that matters, and
[issue 19](../Backlog/Done/19-poc-maui-app.md) for what a better demo would look like.

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
├── Hyperwyc.Sample.ApiService/         # ASP.NET Core API, EF Core over SQL Server
│   ├── Persistence/                    #   ApplicationDbContext
│   └── Services/                       #   ProductService, SalesService
├── Hyperwyc.Sample.Maui/               # .NET MAUI client
├── Hyperwyc.Sample.ServiceDefaults/    # Aspire defaults for the API
├── Hyperwyc.Sample.MauiServiceDefaults/# Aspire defaults for the MAUI app
└── Shared/                             # Product, Sale
```

Aspire brings up SQL Server in a container with a persistent lifetime, so data survives between
runs of the AppHost.

---

## Sample API

Minimal endpoints in `Program.cs`, over EF Core and SQL Server. Aspire provisions the database
container, so there is nothing to install or configure by hand — but Docker (or an equivalent
container runtime) does need to be running.

ASP.NET Core Identity endpoints are mapped alongside, giving the sample a real authentication
surface to exercise Hyperwyc's handler ordering against.

| Method | Route                  | Description                                    |
| ------ | ---------------------- | ---------------------------------------------- |
| `GET`  | `/products`            | The catalogue                                  |
| `GET`  | `/products/{id}`       | A single product                               |
| `POST` | `/products/regenerate` | Build a brand new catalogue without restarting |
| `GET`  | `/sales`               | Sales recorded so far                          |
| `POST` | `/sales`               | Record a sale; decrements stock                |

### The catalogue is randomly generated

Names are assembled from components (`Small-batch Copper Planter`, `Rustic Bamboo Stool`), with
random prices and stock levels, and 8–16 products per catalogue.

This is what makes cache staleness testable. A client holding a cached catalogue keeps showing
the old one — old names, old prices, old stock — until its TTL expires or something invalidates
it. `POST /products/regenerate` lets you provoke that on demand, without restarting anything.

Because the catalogue is persisted, it survives an API restart, so regenerating is the deliberate
lever for staleness rather than a side effect of bouncing the process.

It is generated rather than seeded from a real products API on purpose: a sample about working
without a network should not need one to start.

### Sales decrement stock

This is what makes staleness *consequential* rather than cosmetic. Record a sale and the cached
catalogue is now provably wrong about that product's stock.

It also gives deliberate ways to make a write come back refused, so the rejection path can be
demonstrated on demand:

| Status | Cause                                                          |
| ------ | -------------------------------------------------------------- |
| `400`  | Quantity of zero or less                                       |
| `404`  | Unknown product id                                             |
| `409`  | Insufficient stock — the easiest one to trigger, just oversell |

### Duplicate sales

`POST /sales` deduplicates on `Sale.Id`, which the **client** generates. A sale whose id is
already recorded is returned as-is rather than written again, so stock is not decremented twice.

This is what makes an offline queue safe to flush more than once: a sale that reached the server
but whose response never made it back is recognised on replay.

Note what is *not* involved. Hyperwyc adds no header and asks nothing of this API — it sends the
request the app made. The idempotency is a property of the domain model, not of the transport,
which is the point the sample is making. See
[Duplicate writes](../docs/offline-writes.md#duplicate-writes) for other approaches.

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
- A sale the API refuses surfaces as delivered, carrying the status and body it was refused with

---

## Sync Flow

```
[Record a sale]
      │
      ▼
HyperwycHandler (in the HttpClient pipeline)
      │
      ├── Online?  ──Yes──▶ Send to API ──▶ OnDelivered
      │
      └── Offline? ──────▶ Persist to Cabinet store ──▶ OnQueued
                                   │
                        [Connectivity restored, or "Sync now"]
                                   │
                                   ▼
                   Replay through the same client pipeline,
                   with HyperwycHandler stepping aside
                                   │
                          ┌────────┴────────┐
                   Server answered      No answer at all
                   (2xx, 4xx or 5xx)          │
                          │            Stays queued for
                    OnDelivered,       the next trigger
                    envelope gone       (no event)
```

Note the replay path: a queued sale goes back through the *application's* pipeline, not around
it, so auth and any other handlers apply to it. `HyperwycHandler` recognises the replay and does
not intercept it a second time. See [Auth Handler Placement](../docs/pipeline.md).

---

## Running it

### Prerequisites

- .NET 10 SDK
- .NET MAUI workload (`dotnet workload install maui`)
- A container runtime (Docker Desktop, Podman or similar) — Aspire runs SQL Server in a container
- An Android emulator — the sample targets Android only, since it is the one target that runs
  from a Windows, macOS or Linux development machine

### Steps

```bash
cd sample
dotnet run --project Hyperwyc.Sample.AppHost
```

Aspire starts SQL Server, waits for it to be ready, starts the API, provisions a dev tunnel so
the emulator can reach it, launches the Android emulator and deploys the app. The dashboard shows
logs and traces across all of them.

The database container is marked persistent, so the first run pulls the image and later runs
reuse it.

To drive the API directly while the app is running:

```bash
curl http://localhost:<port>/products
curl -X POST http://localhost:<port>/products/regenerate
```

### Simulating offline

Use the emulator's network toggle, or enable aeroplane mode inside the emulator. Sales recorded
while offline queue locally and replay when connectivity returns.

### Things worth trying

| To see                      | Do this                                                                                             |
| --------------------------- | --------------------------------------------------------------------------------------------------- |
| Offline queueing            | Go offline, record a sale, watch it appear as Queued                                                |
| Automatic replay            | Come back online and wait — no interaction needed                                                   |
| Manual replay               | Come back online and press "Sync now"                                                               |
| Stale cache                 | `POST /products/regenerate`, then browse the catalogue in the app                                   |
| Cache expiry                | Regenerate, then wait out the TTL and refresh                                                       |
| A refused write             | Oversell a product — the `409` comes back on `OnDelivered`, and is final; retrying cannot change it |
| A write that cannot be sent | Stop the API mid-sync — the flush stops and the remaining sales stay queued, with no event          |
| Idempotent replay           | Record a sale offline, come back online, confirm stock drops only once                              |

**Verified on an Android device:** load the catalogue, disable Wi-Fi and mobile data, restart the
app, and the catalogue still loads — served from the Cabinet store by the offline read path. This
is the sample's load-bearing scenario, and the restart is the part that matters: it proves the
cache is durable and correctly located in the app sandbox, not merely held in memory.

---

## Known rough edges

Worth knowing before you conclude something is broken:

- **A sale does not invalidate the cached catalogue.** Write-triggered invalidation matches on
  URL prefix, so `POST /sales` invalidates cached reads under `/sales`, not `/products` — even
  though the sale changed stock levels. Use a short TTL on the catalogue, an explicit refresh,
  or `SyncPolicy.ApiFirst()` for that route. Per-route policies
  ([issue 22](../Backlog/Done/22-v1-per-route-policies.md)) would let this be expressed properly.

---

## Future Sample Targets

| Platform                          | Status                                            |
| --------------------------------- | ------------------------------------------------- |
| .NET MAUI (Android)               | ✅ This sample                                    |
| .NET MAUI (iOS / macOS / Windows) | Should work; not part of the Aspire orchestration |
| Uno Platform / Avalonia / WinUI   | Planned                                           |
| Blazor (WASM + Server)            | Planned — requires an IndexedDB store provider    |
| .NET Nano / embedded              | Exploratory                                       |
