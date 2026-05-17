# Hyperwyc — Proof of Concept

## Purpose

Demonstrate the core functionality of Hyperwyc in a realistic sample environment: an offline-capable .NET MAUI app talking to an ASP.NET Core Web API, with all HTTP traffic running through `HyperwycHandler`. The sample should illustrate Hyperwyc's service-worker-inspired philosophy — the app code makes normal `HttpClient` calls and receives normal-looking responses regardless of connectivity state.

---

## Sample App — .NET MAUI

A simple notes app that exercises all of Hyperwyc's key behaviours:

**Features:**
- Create, update, and delete notes
- View per-item sync state (synced / queued / failed)
- Manually trigger a sync flush
- Observe live sync events in a status panel

**Demonstrates:**
- App code uses standard `HttpClient` calls — no connectivity branching
- Writes queued transparently when the device goes offline (200 OK + `X-Hyperwyc-Status: Queued`)
- Automatic replay when connectivity is restored
- Stale cache served for read operations while offline
- `X-Hyperwyc-Status` header inspected in UI to show sync state
- Dead-letter state surfaced in UI on persistent failure

---

## Sample API — ASP.NET Core Web API

A minimal REST API that serves as the target backend.

**Endpoints:**

| Method | Route | Description |
|--------|-------|-------------|
| `GET` | `/api/notes` | List all notes |
| `POST` | `/api/notes` | Create a note |
| `PUT` | `/api/notes/{id}` | Update a note |
| `DELETE` | `/api/notes/{id}` | Delete a note |
| `POST` | `/api/deadletter` | Receive dead-letter notifications (optional) |

---

## Solution Structure

```
Hyperwyc.Sample
├── MauiApp          # .NET MAUI client application
├── WebApi           # ASP.NET Core backend
└── Shared           # DTOs shared between client and API
```

---

## Sync Flow

```
[User Action]
      │
      ▼
HyperwycHandler (in HttpClient pipeline)
      │
      ├── Online?  ──Yes──▶ Send to API ──▶ Cache response ──▶ OnSynced
      │
      └── Offline? ──────▶ Persist to Cabinet store ──▶ OnQueued
                                   │
                          [Connectivity restored]
                                   │
                                   ▼
                          Replay in queue order
                                   │
                          ┌────────┴────────┐
                        Success           Failure
                          │                 │
                       OnSynced     Polly retry...
                                           │
                                    Max retries hit
                                           │
                                  Dead-letter store
                                           │
                                        OnFailed
```

---

## Running the POC

### Prerequisites

- .NET 10 SDK
- .NET MAUI workload installed (`dotnet workload install maui`)

### Steps

```bash
# Start the API
cd Hyperwyc.Sample/WebApi
dotnet run

# Run the MAUI app (Android emulator, iOS simulator, or desktop)
cd Hyperwyc.Sample/MauiApp
dotnet run -f net10.0-android   # or net10.0-ios / net10.0-maccatalyst / net10.0-windows
```

To simulate offline mode: disable the network adapter or use the emulator's network toggle while the app is running. Notes created while offline will queue and replay automatically when connectivity is restored.

---

## Future Sample Targets

| Platform | Status |
|----------|--------|
| .NET MAUI (primary) | ✅ This POC |
| Uno Platform / Avalonia / WinUI | Planned |
| Blazor (WASM + Server) | Planned — requires `IndexedDbSyncStore` |
| .NET Nano / embedded | Exploratory |
