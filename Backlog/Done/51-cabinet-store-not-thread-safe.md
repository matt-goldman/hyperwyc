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

## Cause — confirmed against Cabinet's source

```csharp
// FileOfflineStore.SaveAsync
var path = Path.Combine(_root, "records", $"{id}.dat.tmp");
await File.WriteAllBytesAsync(path, enc, cancellationToken);
File.Move(path, path.Replace(".dat.tmp", ".dat"), true);
```

The temp file name is **fixed per record-set id**, so two concurrent saves share one path.


Cabinet's `FileOfflineStore.SaveAsync` writes `Envelope.dat.tmp`, then `File.Move`s it over
`Envelope.dat`. That is a correct atomic-write pattern for **one** writer. With two:

1. Save A writes `Envelope.dat.tmp`.
2. Save B writes `Envelope.dat.tmp`, overwriting A's.
3. Save A moves the temp file onto `Envelope.dat`. The temp file no longer exists.
4. Save B moves — and throws `FileNotFoundException`. B's write is lost.

`CabinetSyncStore` had no synchronisation of any kind, so nothing prevented step 2.

An alternative theory — that `SaveAsync` was called with no data and therefore never created the
temp file — was tested and disproved: `File.WriteAllBytesAsync(path, [])` creates the file with
length zero, and `File.Move` on a zero-length file succeeds. The double-move sequence reproduces
the reported exception exactly, message included. Empty data cannot reach it in any case, since
even an empty record set serialises to `[]` and is then encrypted with a nonce and tag.

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

What changed is the *trigger*, not the defect. That is worth taking seriously rather than
asserting, because the reported pattern is a phase change, not a probability shift.

## What this does not explain

Recorded rather than glossed, because the fix being correct is not the same as the story being
complete.

The author reports hitting this **reliably every time** from `269582d` onward, and **reliably
never** before it, through manual testing on device. "Never" to "always" is not what a widened
race window looks like. Something changed deterministically, and the following is what could and
could not be established without a deep dive.

### Ruled out

- **More than one store instance.** This was the one that mattered: a per-instance semaphore over
  two `CabinetSyncStore` objects sharing a directory would narrow the window and *look* fixed.
  It is not the case — the sample calls `AddHyperwyc()` once, and it registers
  `TryAddSingleton<ISyncStore, CabinetSyncStore>`, so there is exactly one instance and the lock
  covers the real object.
- **A reset racing a cache write.** `CabinetSyncStore.ResetAsync` removes records one at a time,
  and each removal triggers a full `SaveAllAsync`, so a reset is a burst of saves that would
  collide with anything else running. Plausible on paper, but `AuthenticationService.Logout()`
  has no call sites anywhere in the sample, so that path never executes. (The save-per-record
  behaviour is still worth fixing on its own merits — see Notes.)
- **A first-draft claim, now retracted:** that the product page's pull-to-refresh was the
  trigger. The timeline does not support it. Refresh arrived in `3eb42c3`, *after* the commit
  the failure was first attributed to.

### Not explained

`269582d` contains no runtime change on the GET path that produced the crash. It touches
`ResetStoreAsync` (never invoked), the `HyperwycService` constructor, and one DI factory line —
and removing `sp.GetRequiredService<ISyncStore>()` from that factory changes neither the number
of store instances nor when the store is constructed, because `SyncOrchestrator` still resolves
it. On inspection that commit should not alter GET behaviour at all.

So either the attribution is approximate by a commit or two, or there is a mechanism not yet
found.

### Leading hypothesis, unproven: the store got slower as it filled

Cabinet's `RecordSet` rewrites the **entire** record set on every single-record change — the
stack above shows `AddAsync` of one envelope going through `SaveAllAsync`. So each cache write
serialises, encrypts and rewrites every envelope already stored. The cost of a write grows with
how much is already cached.

That makes the collision window widen monotonically as the app is used, with no code change at
all. A store small enough that the first save finishes before the second starts becomes a store
where it does not — and once crossed, that threshold does not un-cross itself until app data is
cleared. Never, then always.

It fits the report better than any diff does, and better than the alternative below: it needs no
implausible stall, it is monotonic rather than intermittent, and clearing app data on a
reinstall would reset it — which is enough to make the failure look correlated with whichever
commit happened to coincide with a clean deploy.

Filed separately as [issue 52](../52-store-rewrites-whole-set-per-write.md), because the write
amplification is a defect in its own right regardless of whether it explains this one.

### Secondary hypothesis, weaker than it first looked

`AddStandardResilienceHandler()` is applied to every client by `ServiceDefaults`, and it sits
**outside** `HyperwycHandler` — confirmed by the reported stack, where `ResilienceHandler` calls
`ResolvingHttpDelegatingHandler` calls `HyperwycHandler`. Its standard pipeline is total timeout
→ retry → circuit breaker → **per-attempt timeout**.

When an attempt exceeds the per-attempt timeout, the outer await is abandoned while the inner
`CacheResponseIfEligibleAsync` may still be inside `UpsertAsync`; the retry then begins a fresh
pass through `HyperwycHandler`. That is two concurrent saves **from a single user action**, with
no second caller involved.

The weakness is the numbers. `AddStandardResilienceHandler`'s attempt timeout defaults to ten
seconds, and the sample talks to an API on the same machine as the emulator. A local request
exceeding ten seconds is not impossible — a cold start, a first-request migration — but it is not
the routine event that "every time" would require. Retries on their own do not produce overlap,
because a retried attempt starts only after the previous one has completed; abandonment is what
creates two in-flight passes, and abandonment needs the timeout to fire.

More likely the resilience handler is an amplifier than the cause: each retry is another pass
through `HyperwycHandler`, caching again, growing the store faster and adding save traffic.

**Cheap way to falsify it:** raise or disable the per-attempt timeout in `ServiceDefaults` and
see whether the crash stops. Not run.

### Standing conclusion

The race is real, reproduced, and fixed; the lock is required regardless of what triggered it.
**Why the failure rate changed is open**, with store-growth the leading explanation and the
resilience handler a likely amplifier rather than the cause. If it recurs after this fix, start
from [issue 52](../52-store-rewrites-whole-set-per-write.md).

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
- **`ResetAsync` saves once per record.** It enumerates and calls `RemoveAsync` in a loop, and
  each removal triggers a full `SaveAllAsync` — so clearing a store of two hundred cached
  responses is two hundred write-temp-and-move cycles. Not the cause of this crash, but a
  latent performance problem and a collision source. Clearing the files in one operation would
  fix it, and would also satisfy
  [issue 49](../49-unreadable-store-recovery.md)'s requirement that a reset work on a store that
  cannot be decrypted.
- **Hyperwyc is registered inside the resilience handler in the sample**, contrary to the
  README's "register first" guidance. That means each resilience retry is a separate pass
  through `HyperwycHandler`, each caching its own response. Worth reviewing independently of
  this issue.
- Serialising every read as well as every write is heavier than strictly necessary; a
  reader-writer scheme would allow concurrent reads. Not worth it: `RecordSet` already holds an
  in-memory cache, so reads are cheap, and correctness first.
