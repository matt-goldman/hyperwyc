# Issue 62 — Non-Destructive Reset When the Store Cannot Be Read

## Summary

When the store cannot be read, move it aside and start a clean one, rather than staying in
pass-through for the rest of the session.

Not a destructive reset. The unreadable store is renamed — a `-1` suffix or a `quarantine`
sibling — so nothing is deleted, functionality resumes immediately, and the old bytes stay on
disk for anyone who can do something with them.

## Status

💭 Under consideration. Filed 2026-09-08. **Direction agreed; the bound on accumulation is the
open question.**

## Why the current behaviour is wrong

[Item 49](Done/49-unreadable-store-recovery.md) settled that an unreadable store is a fact to
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
  corruption — see [48](Done/48-exclude-store-from-os-backup.md) for the iOS restore case where the
  derived key stops matching — so the bytes are usually intact and merely unopenable. A developer
  with the key, or a support case with device access, still has them.
- It is the self-healing half of 49 that never shipped. The backlog's own convention note says
  "reinstall after any shape change until 49 makes that self-healing"; 49 made it *reportable*,
  not self-healing. This is the rest.
- It preserves [48](Done/48-exclude-store-from-os-backup.md)'s accidental mitigation: a restored
  backup whose derived key no longer matches gets quarantined rather than replayed, so the
  duplicate-delivery hazard stays closed.

## The accumulation problem

Raised on filing, and the real open question: a device that fails repeatedly accumulates orphaned
stores forever. That is [item 42](42-cache-eviction.md)'s shape arriving by a second route —
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

**1 is the leading candidate**, and the reason is that it is self-limiting rather than bounded
from outside. A store that becomes unreadable twice is a systemic fault, not an incident, and
silently churning stores through it would hide exactly the thing someone needs to see. Falling
back to today's behaviour on the second failure is therefore the right answer *on its own merits*
rather than as a storage cap — and the bound comes free, because **the existence of the
quarantine directory is the counter.** No configuration, no sweep, no scheduler, storage bounded
at 2×.

1 and 5 compose, and probably should.

## Open questions

- **Does this need an option at all?** If quarantine is safe, the argument for
  `ResetStoreOnFailure` disappears — it is just what happens. That is the ADR 0004 reading and
  probably the right one. A consumer wanting the old behaviour has `OnStoreUnreadable` and can
  act on it.
- **Key derivation trap.** The default key is derived from the store path. Quarantining by
  renaming means the orphan's ciphertext was written under the *old* path's key, so anything
  reading it later must derive from the original path, not the quarantine name — and the new
  store must derive from the standard path, not from whatever the rename left behind. Easy to get
  wrong in a way that makes both stores unreadable. Cover it with a test.
- **`ResetStoreAsync` should stay destructive.** An explicit reset is the consumer saying
  discard, which is unambiguous — on logout, quarantining the previous user's outbox is the
  opposite of what was asked. Different verb, different behaviour; worth naming so the two do not
  get conflated during implementation.
- **Where does the orphan live?** A sibling directory keeps `CabinetStoreOptions.DefaultDirectoryPath()`
  meaningful as the thing to exclude from OS backup ([48](Done/48-exclude-store-from-os-backup.md)); a
  suffix on the same directory means the exclusion path needs a wildcard or a second entry.
- **What does the event carry?** [23](23-v1-diagnostics-view.md) is the surface where a
  quarantined store would actually be visible. Sequence with it, or at least do not design the
  event without it in mind.

## Acceptance Criteria

- [ ] An unreadable store does not stop caching and queueing for the session.
- [ ] Nothing is deleted.
- [ ] Repeated failure does not accumulate without bound.
- [ ] The quarantined store is recoverable by someone holding the key, and there is a test
      proving the derivation still works after the rename.
- [ ] `ResetStoreAsync` still discards.
- [ ] `docs/storage.md` says what happens and how to get at a quarantined store.

## Notes

- **Filed with the opposite recommendation and reversed on the same day.** The first version
  argued no, on the grounds that discarding queued writes is the failure the library exists to
  prevent. That reasoning applied a data-loss test to data that was already lost: the `202` had
  been returned, and the writes are unreachable on an end user's device whichever branch is taken.
  Worth keeping, because it is a specific way to get a defaults-test question wrong — asking
  whether something *could* be lost without asking whether it was ever going to be recovered.
- The quarantine option was not in the first version at all. It is the reason the question has an
  answer rather than a trade-off, and it took someone rejecting the framing to find it.
