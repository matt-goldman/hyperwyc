# Issue 62 — Non-Destructive Reset When the Store Cannot Be Read

## Summary

When the store cannot be read, move it aside and start a clean one, rather than staying in
pass-through for the rest of the session.

Not a destructive reset. The unreadable store is renamed — a `-1` suffix or a `quarantine`
sibling — so nothing is deleted, functionality resumes immediately, and the old bytes stay on
disk for anyone who can do something with them.

## Status

✅ **Done.** 2026-09-11. Filed 2026-09-08, direction reversed the same day, and the open question
— the bound on accumulation — settled on **option 1, quarantine once**.

## Why the current behaviour is wrong

[Item 49](49-unreadable-store-recovery.md) settled that an unreadable store is a fact to
report rather than a problem to solve: log it, publish `OnStoreUnreadable`, pass every request
through thereafter. That reasoning holds. What it did not weigh is **where the cost lands.**

The writes being protected are on an end user's device. They will almost never find their way
back to anyone who can act on them — and the `202` was returned when they were queued, so from
the application's point of view they are already lost whichever way this goes. Today's behaviour
does not preserve *delivery*. It preserves forensics, and it charges a working app for it: every
request passes straight through, so caching and queueing are off for the session, on a device
whose owner cannot report the problem and whose developer cannot reach the data.

**The default is technically correct and pragmatically wrong.** It is also
wrong-and-silent in its own way: an end user gets an app that has quietly stopped working
offline, with no path back except a developer having anticipated this and written corrective
action.

And `ResetStoreOnFailure = false` would not fix it, because a default nobody changes is the
behaviour that ships. That is the argument against solving this with an option at all.

## Why quarantine rather than delete

Renaming costs one file operation and removes the entire objection:

- Nothing is destroyed, so the defaults-test question about quietly producing the failure the
  library exists to prevent no longer fires.
- The recoverable case stays recoverable. The usual cause is a key or path change rather than
  corruption — see [48](48-exclude-store-from-os-backup.md) for the iOS restore case where the
  derived key stops matching — so the bytes are usually intact and merely unopenable. A developer
  with the key, or a support case with device access, still has them.
- It is the self-healing half of 49 that never shipped. The backlog's own convention note says
  "reinstall after any shape change until 49 makes that self-healing"; 49 made it *reportable*,
  not self-healing. This is the rest.
- It preserves [48](48-exclude-store-from-os-backup.md)'s accidental mitigation: a restored
  backup whose derived key no longer matches gets quarantined rather than replayed, so the
  duplicate-delivery hazard stays closed.

## The accumulation problem

Raised on filing, and the real open question: a device that fails repeatedly accumulates orphaned
stores forever. That is [item 42](../42-cache-eviction.md)'s shape arriving by a second route —
unbounded growth with nothing removing anything — and on Android it runs into the 25 MB backup
quota noted in `docs/storage.md`.

Options, roughly in order of how much machinery they need:

1. **Quarantine once.** If no quarantined store exists, move the current one aside and start
   clean. **If one already exists, do not** — report and step aside, exactly as today.
2. **Keep one, overwriting.** Bounded at 2×, but the second failure destroys the first orphan,
   which is usually the most diagnostic.
3. **Rotate N.** More forensics, still bounded, more machinery and a naming scheme.
4. **Age or size bound.** Needs a sweep, and startup is the only place Hyperwyc runs code that
   is not a request. Couples this to 42.
5. **Bound nothing; report it.** Publish the orphan count and size on `OnStoreUnreadable` and let
   the application decide. Consistent with "report and get out of the way", and pushes the policy
   to whoever knows the device.

**1 is what shipped**, and the reason is that it is self-limiting rather than bounded
from outside. A store that becomes unreadable twice is a systemic fault, not an incident, and
silently churning stores through it would hide exactly the thing someone needs to see. Falling
back to today's behaviour on the second failure is therefore the right answer *on its own merits*
rather than as a storage cap — and the bound comes free, because **the existence of the
quarantine directory is the counter.** No configuration, no sweep, no scheduler, storage bounded
at 2×.

1 and 5 compose, and probably should.

## How the open questions were answered

**Does this need an option at all?** No, and there isn't one. Quarantine is just what happens when the store cannot be read. A consumer who wants the old behaviour still has `OnStoreUnreadable`, which now means *and it could not be set aside* rather than *and nothing was tried*.

**Key derivation trap.** Real, and it needed public API to close rather than a comment. `CabinetStore.OpenQuarantined(options)` takes the options the *live* store was configured with and opens the quarantine directory under the key derived from the original path; opening that directory as an ordinary store derives the wrong key and finds it unreadable, which looks exactly like the damage that caused the quarantine. Both halves are tested — the right way round works, and the naive way round still fails, which is what makes the method worth having rather than a convenience.

`CabinetStore.QuarantinePath(options)` is the other half: without it a consumer who has recovered what they wanted has no supported way to delete the orphan, and until it is gone a second failure cannot be quarantined.

**`ResetStoreAsync` stays destructive**, and takes the quarantine with it. Leaving one behind would keep the previous user's queued writes on disk through the logout the method exists for. It also re-arms the quarantine, which falls out of the same reasoning: the counter goes with the contents it was counting.

**Where the orphan lives.** `{DirectoryPath}/quarantine`, a directory *inside* the store directory rather than a sibling of it. That keeps `CabinetStoreOptions.DefaultDirectoryPath()` the single path to exclude from OS backup ([48](48-exclude-store-from-os-backup.md)) — a quarantined outbox is exactly as unwelcome in a restore as a live one — where a `-1` suffix would have needed a wildcard or a second entry. Cabinet only ever touches `records/`, `index/` and `attachments/` under its root, so nothing else has to know the directory is there.

**What the event carries.** Nothing, like `OnStoreUnreadable`, for the same reason: it is a signal an application can act on while the diagnosis goes to the log. A new `OnStoreQuarantined` rather than a reuse, because the two say opposite things about whether the session still works, and [23](23-v1-diagnostics-view.md) needs to tell them apart — an outbox that is empty because the previous one was set aside is not an outbox that is empty because nothing was queued.

## What it took beyond the obvious

**The generation counter is the part that was not in the design.** Two requests in flight both hit the broken store and both throw; the first quarantines and recovers, and the second — reporting a failure that is already history — would latch the session off a moment later, undoing the whole feature in the most common case there is. So `StoreHealth.Generation` is read *before* a store operation and handed back with the report, and a report carrying a stale generation is dropped. Found by reasoning about the concurrency the store's own `SemaphoreSlim` guarantees, and covered by a test that fires eight overlapping requests at a broken store.

**`TryQuarantineAsync` has a default implementation returning `false`.** Every existing `IHyperwycStore` keeps the [49](49-unreadable-store-recovery.md) behaviour unchanged and unbroken, and a store with nowhere to put its contents — `InMemoryStore`, anything backed by something that cannot rename — says so by saying nothing. Per [ADR 0009](../../docs/decisions/0009-provide-the-seam-not-the-alternatives.md), the contract documentation is the support, so `storage.md` carries what an implementer owes.

**A failed quarantine is treated exactly as a declined one.** `StoreHealth` catches whatever the store throws, because it is running inside a `catch` block on the consumer's own HTTP request — an exception escaping there would surface out of `HttpClient.SendAsync`, which is the [49](49-unreadable-store-recovery.md) defect reappearing in the one place least likely to be looked at.

**The request that discovered the problem still degrades**, and deliberately is not retried against the clean store. It already has an answer it can give — the truth, that Hyperwyc is not holding the write — and that is visible and recoverable. The next request queues normally.

## Acceptance Criteria

- [x] An unreadable store does not stop caching and queueing for the session.
- [x] Nothing is deleted.
- [x] Repeated failure does not accumulate without bound.
- [x] The quarantined store is recoverable by someone holding the key, and there is a test
      proving the derivation still works after the rename.
- [x] `ResetStoreAsync` still discards.
- [x] `docs/storage.md` says what happens and how to get at a quarantined store.

## Notes

- **Filed with the opposite recommendation and reversed on the same day.** The first version
  argued no, on the grounds that discarding queued writes is the failure the library exists to
  prevent. That reasoning applied a data-loss test to data that was already lost: the `202` had
  been returned, and the writes are unreachable on an end user's device whichever branch is taken.
  Worth keeping, because it is a specific way to get a defaults-test question wrong — asking
  whether something *could* be lost without asking whether it was ever going to be recovered.
- The quarantine option was not in the first version at all. It is the reason the question has an
  answer rather than a trade-off, and it took someone rejecting the framing to find it.
- **Options 1 and 5 were expected to compose, and 5 turned out not to be needed.** Publishing the orphan count and size would be reporting on a number that is only ever zero or one, and a size nobody can act on without the key. `OnStoreQuarantined` says the thing that is actually actionable — it happened — and `CabinetStore.OpenQuarantined` is how an application that cares looks at what is in it.
