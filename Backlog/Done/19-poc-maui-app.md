# Issue 19 — POC: .NET MAUI Sample App

## Summary

Build the .NET MAUI client application that exercises Hyperwyc's core behaviours end-to-end against the sample API (issue #18).

## Background

The MAUI app is the primary POC surface. It gives developers a concrete reference for wiring Hyperwyc into a real app and demonstrates every major feature: online sync, offline queue, retry, dead-letter, and event observation.

## Features

| Feature | Hyperwyc Behaviour Demonstrated |
|---|---|
| Create / update / delete notes | Mutating requests; online and offline paths |
| Per-item sync state badge | `OnQueued`, `OnSynced`, `OnFailed` events |
| Live sync event log panel | Full `SyncEvents` subscription |
| "Sync Now" button | `IHyperwyc.FlushAsync()` |
| "Logout / Clear Cache" button | `IHyperwyc.ResetStoreAsync()` |
| Offline simulation instructions | README note on using emulator network toggle |

## Wiring

```csharp
// MauiProgram.cs
builder.Services.AddHttpClient("NotesApi", c => c.BaseAddress = new Uri("http://localhost:5000"))
    .AddHyperwycHandler();

builder.Services.AddHyperwyc(options =>
{
    options.DefaultPolicy = SyncPolicy.CacheFirst(TimeSpan.FromMinutes(5));
    options.Store = new CabinetSyncStore("notes.db");
    options.Connectivity = new MauiConnectivityService();
});
```

## Acceptance Criteria

Retargeted from the original notes domain to products and sales, matching
[issue 18](18-poc-web-api.md).

- [x] `Hyperwyc.Sample.Maui` project created, targeting `net10.0-android` plus iOS, MacCatalyst
      and Windows where the host supports them.
- [x] Login, catalogue, new-sale and sales pages, with view models.
- [x] `MauiConnectivityService` wired as `HyperwycOptions.Connectivity` (issue #13).
- [x] Both API clients registered with `AddHyperwycHandler()` ahead of the auth handler.
- [x] **Catalogue reads served from cache with the device offline, across an app restart.**
- [x] Recording a sale offline queues it and surfaces as pending.
- [x] Queued sales replay when connectivity returns.
- [x] Live event log subscribed to `IHyperwyc.SyncEvents`.
- [x] "Sync now" calls `IHyperwyc.FlushAsync()`.
- [x] "Clear data" calls `IHyperwyc.ResetStoreAsync()`.
- [x] A sale the server refuses surfaces as dead-lettered, with the server's reason shown.
      Ordering more than the catalogue has in stock is the easiest way in: the write queues
      offline, replays, and comes back `409` with "Only N left in stock" — the scenario
      [issue 40](40-surface-deferred-outcomes.md) was written around. Part of per-sale
      state above rather than separate work.
- [x] `POC.md` documents prerequisites and run steps.
- [-] ~~Per-sale sync state — blocked on [issue 40](40-surface-deferred-outcomes.md), since an event cannot currently be attributed to a specific sale.~~ Deprecated.

## Proven so far

The load-bearing scenario works on an Android device: start the app, load the catalogue from the API, disable Wi-Fi and mobile data, restart the app, and the same call returns the cached catalogue.

The restart is what makes it significant. Serving from cache while running would demonstrate only an in-memory cache; surviving process death demonstrates three things at once that were assumptions until now — that Cabinet persisted to a stable, app-sandboxed location, that the path-derived encryption key round-trips across process lifetimes, and that the offline read path is reached rather than the online path happening to find a fresh entry.

## Notes

- UI does not need to be polished — this is a developer reference, not a shipping app.
- The same `NoteDto` shared project from issue #18 is used here.
- The dead-letter state ("Failed" badge) can be triggered by stopping the API server after queueing notes.

## It is a proof of concept, not a demo

Recorded 2026-08-28. The sales domain was chosen for convenience and turned out to be a poor
showcase: stock level is a shared mutable resource, so an offline sale is conflict-prone by
construction and the `409` is routine rather than illustrative. It has done its job — it proved
offline reads, offline writes, replay on reconnect, and binary bodies on a real device — and
that job is validation, not demonstration.

Two follow-ups, neither urgent:

- **Rename it to reflect what it is.** `Hyperwyc.Sample.Maui` is really `Hyperwyc.Poc.Maui`.
- **A demo worth showing needs a different domain** — append-only, one writer per record. The
  inspection app sketched in [issue 50](../50-resilient-applications-guide.md) is the candidate,
  and it comes from real client work rather than being invented to suit the library.

Remaining UI polish is deliberately parked until per-route policies
([22](22-v1-per-route-policies.md)) land, since interplay between routes may dissolve some of
it.
