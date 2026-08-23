# Issue 49 — What Happens When the Store Cannot Be Decrypted

## Summary

Hyperwyc has no answer for a store it cannot read: a key mismatch surfaces as an unhandled
`CryptographicException` from wherever the store was first touched, including out of the
consumer's own `HttpClient.SendAsync`.

**Report it and carry on.** Log through `ILogger` if one is available, publish an event, degrade
to an empty store, and stop there. Do not throw, do not delete, do not attempt recovery.

## Status

⬜ Open. Filed 2026-08-23. The question predates
[issue 48](48-exclude-store-from-os-backup.md); 48 only supplied a likely trigger.

## What happens today

Nothing catches it. `CabinetSyncStore` calls `RecordSet<Envelope>.GetAllAsync`, Cabinet decrypts
with `AesGcmEncryptionProvider`, and a wrong key fails the AES-GCM authentication tag. There is
no `catch` for `CryptographicException` anywhere in the codebase.

Where it lands depends on which call touches the store first:

| First touch | Where the exception appears |
|---|---|
| `HyperwycHostedService.StartAsync` (the startup flush) | Host startup, before the app is running |
| `HyperwycHandler` on a read | **Out of the consumer's `HttpClient.SendAsync`** |
| `HyperwycHandler` on an offline write | Out of `SendAsync`, and the write is lost |

The second is the defect. A caller doing `GetFromJsonAsync<Product[]>()` has no reason to expect
a cryptography exception, and nothing at the call site suggests the problem is a local store
rather than the request they just made. Hyperwyc is a transport-level component; leaking a
storage implementation's exception through an HTTP call is a boundary violation regardless of
what policy is chosen for the underlying condition.

## Decision: report and degrade

This is a **transport-level** tool. It does not guarantee delivery, in the same way and for the
same reason that it takes no position on sync conflict resolution or duplicate suppression
([ADR 0001](../docs/decisions/0001-idempotency-is-not-hyperwycs-remit.md)). An unreadable store
means Hyperwyc has less to work with; it does not make Hyperwyc responsible for putting it right.

So, on a decryption failure:

1. **Log it**, through an optional `ILogger` if the container has one.
2. **Publish an event**, so an application watching `SyncEvents` can react.
3. **Return what can be read**, which may be nothing.
4. **Nothing else.** No exception, no deletion, no quarantine, no recovery attempt.

Uniform across every case. Reads degrade to empty; the application is told; it decides.

### What this replaces

An earlier draft of this item proposed a policy that branched on whether the key was derived or
supplied — recreate the store in the first case, throw in the second — on the reasoning that the
derived key is unrecoverable by construction while a supplied key might simply be the wrong one.

The analysis was right and the conclusion was out of scope. Both branches have Hyperwyc taking
custody of a decision that is not its own:

- **Recreating** destroys the consumer's data on their behalf. Even unreadable data is theirs, and
  deleting it is irreversible.
- **Throwing** bricks the application. Refusing to start because local storage is damaged is a
  policy an application might reasonably choose, but Hyperwyc choosing it for them is worse than
  either alternative.

Reporting and degrading is the option that requires no opinion about whose data it is or how much
the application can tolerate losing.

The distinction between derived and supplied keys survives, but only as **what the log message
says** — "the derived key does not match, which usually means the store directory moved" against
"the supplied key does not match this store" — not as a difference in behaviour. That is a much
smaller thing to get right, and it cannot do any damage if it is wrong.

### The remedy already exists, and belongs to the application

`IHyperwyc.ResetStoreAsync()` ([issue 16](16-reset-store-async.md)) already clears the store. That
is the recovery action, it is already in the public surface, and it is the application's to call.

Hyperwyc reports that the store is unreadable. An application that wants a clean slate calls
`ResetStoreAsync`. An application that would rather preserve the files for support, or prompt the
user, or refuse to run, does that instead. None of those are transport concerns, and Hyperwyc
does not need to know which one was chosen.

This is worth stating in the README next to the event, because otherwise the obvious reading of
"reports and carries on" is that nothing can be done.

## Partial failure needs nothing extra

A useful consequence of the decision.

An earlier draft treated "every document fails" (key mismatch) and "one document fails"
(corruption) as cases needing to be told apart, since one warranted recreating the store and the
other emphatically did not. That mattered only because a destructive action was on the table.

Under report-and-degrade the same rule covers both: **skip what cannot be read, report it, return
the rest.** One bad document loses one envelope. A wholly unreadable store reads as empty. No
branch, no distinction, nothing to get wrong.

It also removes an upstream blocker. Cabinet's `GetAllAsync` currently fails fast, so Hyperwyc
cannot skip individual documents — but it can catch at its own boundary and degrade the whole
read to empty, which is the same policy at coarser granularity. Per-document granularity becomes
a **refinement** Cabinet could enable later, rather than something this issue waits on.

## An unusable store degrades to pass-through, not to a half-working one

Settled rather than left open, because the answer follows from the one promise Hyperwyc does
make.

Reads degrading to empty is straightforward. Writes are not, and the tempting answer — keep
accepting them into a store whose reads just failed — is wrong. Cabinet may hold an in-memory set
seeded from disk, so a write might fail, might land alongside the unreadable documents, or might
clobber an index. Whichever it is, the write may not be there after a restart.

**That breaks the only guarantee Hyperwyc offers.** Queueing a write means it survives the
process ending, and on mobile the process ending is routine rather than exceptional — the app is
backgrounded and reaped between the user tapping "submit" and looking at their phone again. A
store that accepts writes it cannot reliably persist fails at exactly the moment it exists for,
and fails silently.

The synthetic `202` is where this bites. `202 Accepted` is Hyperwyc's promise that it has taken
custody of the request. **Returning it when the store could not accept the envelope is a lie**,
and a worse outcome than any error, because the application stops tracking a write that is not
going to happen.

So the store either works or it does not. When it does not, Hyperwyc degrades to **pass-through**:
it forwards requests and adds nothing. No cache lookups, no queueing, no synthetic responses.
Offline, an application then sees the transport failure it would have seen without Hyperwyc
installed — which is the truth, and is recoverable, where a `202` is neither.

This needs the handler to know the store is unusable, so the latch below is load-bearing rather
than merely a log-volume optimisation.

## Reporting the failure

`ILogger` is the primary channel, since this is diagnostic. `Microsoft.Extensions.Logging.Abstractions`
is already in `Hyperwyc.Core`'s dependency graph transitively via `Microsoft.Extensions.Hosting.Abstractions`,
so a direct reference costs nothing new. Inject `ILogger<T>?` and no-op when absent — Hyperwyc
must stay usable without a container.

The event is the programmatic channel, and is the part that needs design. Every existing
`SyncEventType` describes one request moving through its lifecycle; this describes the store. See
Open Questions.

**Report once, not per read.** A latch on the store instance, so an application whose every read
degrades does not get one log line and one event per HTTP call. The condition does not change
until the process restarts or the store is reset.

## Open Questions

1. **What does the event look like?** `SyncEvent` is built around a request — `Url`, `Method`,
   `CorrelationId`, `RequestBody` are all meaningless here. Options: a new `SyncEventType` with
   those fields null and the detail in a new field; a separate `IObservable` for store-level
   events; or logging only, with no event at all. Leaning toward a new `SyncEventType`, accepting
   that the stream becomes "things that happened" rather than "things that happened to a request",
   because a second observable is a worse thing to ask a consumer to remember to subscribe to.
2. **Should `ResetStoreAsync` be able to clear a store it cannot read?** It presumably deletes
   files rather than records, in which case yes and this is free. Worth confirming, since the
   recommended remedy is useless if it needs to decrypt first. **This is
   [issue 16](16-reset-store-async.md)'s to answer** — it is already doing the work on that
   method.

## The better fix, upstream of all of this

Worth restating because it reduces how often any of this is reached.

The derived key fails because it is `SHA256` of a **volatile absolute path**. On iOS that path
contains the app container UUID, which changes on reinstall or restore
([issue 48](48-exclude-store-from-os-backup.md)). Deriving from something stable instead — a fixed
salt plus an application identity — makes the store readable across exactly the events that
currently break it, leaving genuine corruption as the only trigger.

That belongs to [issue 32](32-default-encryption-key.md) and should be settled first. No
compatibility cost; nothing is released.

## Acceptance Criteria

- [ ] A decryption failure never escapes `CabinetSyncStore` as a raw `CryptographicException`,
      and never escapes through `HttpClient.SendAsync`.
- [ ] Reads degrade to what can be read — nothing, in the whole-store case — rather than throwing.
- [ ] The failure is logged through an optional `ILogger`, with a message that distinguishes a
      derived-key mismatch from a supplied-key one.
- [ ] The failure is published as an event an application can observe.
- [ ] Reported once per store instance, not once per read.
- [ ] Nothing is deleted, moved or recreated by Hyperwyc.
- [ ] An unusable store degrades to pass-through: no cache lookups, no queueing, and **no
      synthetic `202`** for a write Hyperwyc cannot persist.
- [ ] Decision recorded on each open question.
- [ ] Tests: a store opened with the wrong key reads as empty rather than throwing; the event
      fires; the log is written; it is reported once across several reads; nothing on disk is
      removed; an offline write against an unusable store does **not** receive a `202`.
- [ ] README documents the behaviour and names `ResetStoreAsync` as the application's remedy.

## Notes

- Raised by the author while reviewing [issue 48](48-exclude-store-from-os-backup.md): "how do we
  handle a failed decryption... this question should have existed before we knew this anyway."
- **The scope correction is the point of this item.** The first draft reasoned its way to a
  branching recovery policy because the analysis of *recoverability* was interesting. Interesting
  analysis is not a mandate: Hyperwyc is transport-level, guaranteed delivery is not its promise,
  and an unreadable store is a fact to report rather than a problem to solve. Worth an ADR if
  this shape recurs — it is the same failure mode as ADR 0001, arrived at from a different
  direction.
- Suggested milestone **v1.0** for the boundary fix specifically — a raw `CryptographicException`
  out of an HTTP call is a defect, and it is small. The event and the logging can follow.
- Sequencing: settle [32](32-default-encryption-key.md)'s derivation first.
- **This item is captured, not scheduled.** The remaining open questions are deliberately open:
  the shape of a store-level event is a design decision worth taking with the diagnostics work
  ([23](23-v1-diagnostics-view.md)) in view rather than in isolation, and the `ResetStoreAsync`
  question belongs to [16](16-reset-store-async.md). Nothing here needs solving before v1.0
  except the boundary fix.
- Guidance for applications that need genuine delivery guarantees — which generally means an
  application-owned store alongside Hyperwyc's — belongs in
  [issue 50](50-resilient-applications-guide.md), not here.
