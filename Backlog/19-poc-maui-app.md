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
| "Sync Now" button | Manual flush trigger via `IHyperwyc` |
| "Logout / Clear Cache" button | `IHyperwyc.ResetStoreAsync()` |
| Offline simulation instructions | README note on using emulator network toggle |

## Wiring

```csharp
// MauiProgram.cs
builder.Services.AddHttpClient("NotesApi", c => c.BaseAddress = new Uri("http://localhost:5000"))
    .AddHttpMessageHandler<HyperwycHandler>();

builder.Services.AddHyperwyc(options =>
{
    options.DefaultPolicy = SyncPolicy.CacheFirst(TimeSpan.FromMinutes(5));
    options.Store = new CabinetSyncStore("notes.db");
    options.Connectivity = new MauiConnectivityService();
});
```

## Acceptance Criteria

- [ ] `Hyperwyc.Sample/MauiApp` project created; target `net10.0-android` and `net10.0-windows` at minimum.
- [ ] `NoteListPage` showing all notes with sync-state badges.
- [ ] Create/edit/delete notes flow fully functional.
- [ ] Live event log panel subscribes to `IHyperwyc.SyncEvents` and appends entries.
- [ ] "Sync Now" button calls the orchestrator flush manually.
- [ ] "Logout" button calls `IHyperwyc.ResetStoreAsync()` and clears the note list.
- [ ] App runs offline: notes created while offline appear with "Queued" badge.
- [ ] On reconnect, queued notes sync and badges update to "Synced".
- [ ] A note that fails all retries shows a "Failed" badge.
- [ ] `POC.md` in the repo root documents prerequisites and run steps.

## Notes

- UI does not need to be polished — this is a developer reference, not a shipping app.
- The same `NoteDto` shared project from issue #18 is used here.
- The dead-letter state ("Failed" badge) can be triggered by stopping the API server after queueing notes.
