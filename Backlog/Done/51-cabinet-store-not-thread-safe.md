# Issue 51 — `CabinetSyncStore` Was Not Safe for Concurrent Use

## Summary

`CabinetSyncStore` serialised nothing, while the handler that uses it is transient and runs on
arbitrary threads. Two overlapping requests caching a response at the same time crashed with
`FileNotFoundException` from inside Cabinet's save, losing the write.

## Status

✅ **Done.** Fixed 2026-08-24.

## The report

Hit in the MAUI sample on Android:

```
System.IO.FileNotFoundException: Could not find file
  '/data/user/0/com.companyname.Hyperwyc.Sample.Maui/files/Hyperwyc/records/Envelope.dat.tmp'
   at System.IO.File.Move(String sourceFileName, String destFileName, Boolean overwrite)
   at Cabinet.Core.FileOfflineStore.SaveAsync<List<Envelope>>
   at Cabinet.Core.RecordSet<Envelope>.SaveAllAsync
   at Cabinet.Core.RecordSet<Envelope>.AddAsync
   at Hyperwyc.Cabinet.CabinetSyncStore.UpsertAsync
   at Hyperwyc.HyperwycHandler.CacheResponseIfEligibleAsync
   at Hyperwyc.HyperwycHandler.HandleOnlineReadAsync
```

## Cause

Cabinet's `FileOfflineStore.SaveAsync` writes `Envelope.dat.tmp`, then `File.Move`s it over
`Envelope.dat`. That is a correct atomic-write pattern for **one** writer. With two:

1. Save A writes `Envelope.dat.tmp`.
2. Save B writes `Envelope.dat.tmp`, overwriting A's.
3. Save A moves the temp file onto `Envelope.dat`. The temp file no longer exists.
4. Save B moves — and throws `FileNotFoundException`. B's write is lost.

`CabinetSyncStore` had no synchronisation of any kind, so nothing prevented step 2.

The file corruption is the loud symptom. The quiet one is that every mutating method is a
read-modify-write — `UpsertAsync`, `MarkSyncedAsync`, `MoveToDeadLetterAsync`,
`InvalidateCacheForPrefixAsync` all `GetByIdAsync` then `UpdateAsync` — so two concurrent
callers could each read the same envelope and the second write would silently discard the
first. That one produces no exception at all.

## Why it took this long to surface

Concurrency here is ordinary, not exotic: `HyperwycHandler` is registered transient and runs on
whatever thread its caller used, so any two overlapping HTTP requests reach the store together,
and an orchestrator flush runs on a background task alongside them.

Two things hid it.

**The test suite only ever exercised `InMemorySyncStore`**, which serialises every operation
behind a `SemaphoreSlim`. Every store-level test therefore ran against an implementation that
already had the property the other one lacked, and passed. The two implementations were never
held to the same contract, because the contract was never written down.

**`ISyncStore` never stated a thread-safety requirement.** With nothing in the interface saying
concurrent writes must be safe, `InMemorySyncStore` having a lock reads as an implementation
detail rather than as conformance, and `CabinetSyncStore` not having one reads as a reasonable
choice.

## Not introduced by [issue 16](16-reset-store-async.md)

Suspected, and checked: `git log -S "SemaphoreSlim"` over `CabinetSyncStore.cs` returns nothing,
so the file has had no synchronisation since it was created in `574babc`. Issue 16 did not touch
it. Removing `sp.GetRequiredService<ISyncStore>()` from the `HyperwycService` factory does not
change how many store instances exist either — the store is a singleton and `SyncOrchestrator`
still resolves it, so it is constructed exactly once, at the same point, as before.

What likely changed is the *trigger*, not the defect: the sample gained a refresh on the product
page, which makes overlapping GETs easy to produce by hand.

## The fix

A single `SemaphoreSlim(1, 1)` in `CabinetSyncStore`, taken by every `ISyncStore` method —
the same shape `InMemorySyncStore` already used, deliberately, rather than anything cleverer. No
method calls another, so there is no re-entrancy to worry about.

`ISyncStore` now states the requirement: implementations must be safe for concurrent use,
including concurrent writes, with the reason given so it does not read as boilerplate.

## Acceptance Criteria

- [x] Every `CabinetSyncStore` operation is serialised.
- [x] `ISyncStore` documents that implementations must be safe for concurrent use, and why.
- [x] Tests reproduce the failure: concurrent upserts, concurrent upserts of the same URL,
      interleaved reads and writes, concurrent upsert and reset, and concurrent `MarkSyncedAsync`
      for the lost-update case.
- [x] Those tests fail against the unsynchronised store — verified by neutralising the semaphore
      and confirming all five fail on three consecutive runs.

## Notes

- The tests live in `Hyperwyc.Tests` rather than `Hyperwyc.Core.Tests`, since they need the
  Cabinet package. That is the same split that let the gap exist, so it is worth saying plainly:
  **a store-level behaviour proven only against `InMemorySyncStore` is not proven.** Anything
  that is part of the `ISyncStore` contract wants exercising against both.
- Worth considering a shared conformance suite both implementations run, which would have caught
  this. Not done here — it is a larger change than the fix, and it should be filed on its own
  merits rather than smuggled in with a crash fix.
- Serialising every read as well as every write is heavier than strictly necessary; a
  reader-writer scheme would allow concurrent reads. Not worth it: `RecordSet` already holds an
  in-memory cache, so reads are cheap, and correctness first.
