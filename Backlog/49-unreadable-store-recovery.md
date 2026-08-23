# Issue 49 — What Happens When the Store Cannot Be Decrypted

## Summary

Hyperwyc has no answer for a store it cannot read. A key mismatch surfaces as an unhandled
`CryptographicException` from wherever the store was first touched — including out of the
consumer's own `HttpClient.SendAsync`. Decide the policy, distinguish recoverable from
unrecoverable, and make the failure land somewhere it can be understood.

## Status

⬜ Open. Filed 2026-08-23. The question predates
[issue 48](48-exclude-store-from-os-backup.md); 48 is only what made it visible.

## What happens today

Nothing catches it. `CabinetSyncStore` calls `RecordSet<Envelope>.GetAllAsync`, Cabinet decrypts
with `AesGcmEncryptionProvider`, and a wrong key fails the AES-GCM authentication tag. There is
no `catch` for `CryptographicException` anywhere in the codebase.

Where it lands depends on which call touches the store first, and none of the options are good:

| First touch | Where the exception appears |
|---|---|
| `HyperwycHostedService.StartAsync` (the startup flush) | Host startup, before the app is running |
| `HyperwycHandler` on a read | **Out of the consumer's `HttpClient.SendAsync`** |
| `HyperwycHandler` on an offline write | Out of `SendAsync`, and the write is lost |

The second is the one to fix regardless of policy. A caller doing `GetFromJsonAsync<Product[]>()`
has no reason to expect a cryptography exception, and no way to tell from the call site that the
problem is a local store rather than the request they just made.

Fixing only that — catching at the `CabinetSyncStore` boundary and rethrowing as a typed
Hyperwyc exception with a message that names the store path — is worth doing on its own even if
every other question here is deferred.

## The rule: recoverable or not, rather than which key

The instinct is to split on where the key came from: throw for a supplied key, recreate for the
derived one. That reaches the right answer, but the reason generalises better if it is stated as
**can anyone still read this data?**

**Derived key — unrecoverable by construction.** The key is `SHA256(DirectoryPath)`, a pure
function of a value Hyperwyc already has. If decryption fails, there is no key the consumer could
supply that would work. Throwing hands them an error they can do nothing about, and — since it
throws again on every subsequent launch — an application that can never start.

**Supplied key — recoverable in principle.** The correct key exists outside Hyperwyc: in secure
storage, in a config file, in a password manager. A mismatch usually means the wrong key was
passed, not that the data is worthless. Destroying it would be irreversible, and would destroy
data whose owner was doing the more careful thing.

Stated as recoverability rather than provenance, the rule also answers cases not yet built. The
`SecureStorage`-backed key in [issue 32](32-default-encryption-key.md) is supplied-from-outside,
so it throws — even though the consumer never typed it.

## Recreate, but never silently

For the derived-key case the recreate is essentially forced: the alternative is an application
that cannot start. The open part is not *whether* to recreate but whether the consumer is told,
and here [ADR 0003](../docs/decisions/0003-default-what-you-can-decide-correctly.md)'s defaults
test is decisive.

> 2. If the default is wrong, does the consumer find out? Wrong-and-loud is fine; wrong-and-silent
>    ships.

A silent recreate discards a pending outbox — queued writes the user was told had been accepted —
with no signal at all. That the data was *already* lost is true and beside the point: the data is
unrecoverable, but the knowledge that it was lost is not, and that is the part still worth
preserving. An application that knows can prompt, re-submit from its own records, or at minimum
stop showing an order as pending.

So: recreate, and report. Reporting needs a channel, which is the main design question below.

## Not every failure should condemn the whole store

An important distinction, and one that decides how much of this is even Hyperwyc's to build.

- **Every document fails** → a key mismatch. The store is uniformly unreadable and recreating it
  is the only way forward.
- **One document fails** → corruption of a single file, a partial write, a bad sector. Recreating
  the entire store here would be an appalling overreaction: it would discard every other queued
  write to salvage nothing.

The right behaviour for a single bad document is to skip it, report it, and carry on — the store
keeps working and one envelope is lost instead of all of them.

Whether that is *possible* depends on Cabinet. `RecordSet<T>.GetAllAsync` currently surfaces the
first decryption failure as an exception, so Hyperwyc cannot tell the two cases apart: it sees an
exception either way. Distinguishing them needs Cabinet to be able to enumerate with
skip-and-report semantics rather than fail-fast.

That makes part of this an upstream question rather than a Hyperwyc one, and it should be settled
before the policy is implemented — a policy that cannot tell "the key is wrong" from "one file is
damaged" will get the damaged-file case badly wrong.

## Where the failure should be detected

Store construction is deliberately lazy ([issue 31](Done/31-package-structure.md)), so nothing
touches the filesystem at registration and the first failure lands wherever the first read
happens to be.

Options, roughly in order of preference:

1. **Probe once at startup.** Register `HyperwycHostedService` unconditionally and have it read
   the store before deciding whether to flush, so the failure surfaces during host startup where
   it can be reasoned about. Currently the hosted service is only registered when
   `FlushOnStartup` is true, so a consumer who turned that off has no probe point at all. A small
   change, and it puts the error somewhere sensible.
2. **Convert at the boundary.** Catch in `CabinetSyncStore` and rethrow typed. Necessary anyway;
   not sufficient, because it still surfaces inside an HTTP call.
3. **Leave it lazy and document it.** Cheapest, and the worst experience.

1 and 2 are complementary and should both happen.

## Open Questions

1. **What channel reports a recreated store?** `SyncEvents` is the obvious one, but every existing
   `SyncEventType` describes a single request moving through its lifecycle, and this describes the
   store. A `StoreReset`-style member would be the first event that is not about a request. The
   alternatives are an `ILogger` line (easy to miss, and Hyperwyc takes no logging dependency
   today) or a callback on `HyperwycOptions` (explicit, but a different shape from everything
   else). Leaning toward the event, and accepting that the event stream becomes "things that
   happened", not "things that happened to a request".
2. **Should the policy be configurable?** ADR 0003 argues against an option where the correct
   answer is knowable, and it is knowable here. But a consumer on the derived key might still
   prefer to fail loudly rather than lose an outbox quietly, even knowing it is unrecoverable.
   Resist until someone asks.
3. **Quarantine instead of delete?** Renaming the unreadable directory aside rather than deleting
   it converts an irreversible act into a reversible one. Mostly pointless for the derived-key
   case — though not entirely, since `SHA256(oldPath)` is computable by anyone who knows the old
   path. It leaves orphaned data on a phone forever unless bounded to a single generation.
4. **Does Cabinet need to change?** See the section above. Likely yes, for the
   partial-failure case.

## The better fix, upstream of all of this

Worth stating because it would shrink this issue considerably.

The derived key fails in the first place because it is `SHA256` of a **volatile absolute path**.
On iOS that path contains the app container UUID, which changes on reinstall or restore
([issue 48](48-exclude-store-from-os-backup.md)). Deriving from something stable instead — a
fixed salt plus an application identity — would make the store readable across exactly the events
that currently break it.

That would leave the derived-key case failing only on genuine corruption, which is rare and is
the single-document case rather than the whole-store one. The policy question does not disappear,
but it stops being something a normal iOS restore triggers.

This belongs to [issue 32](32-default-encryption-key.md) and should be decided there first. There
is no compatibility cost — nothing is released.

## Acceptance Criteria

- [ ] A decryption failure never escapes as a raw `CryptographicException`; it is a typed
      Hyperwyc exception naming the store path and the likely cause.
- [ ] A decryption failure never escapes through `HttpClient.SendAsync` unexplained.
- [ ] Derived key + wholly unreadable store → recreated, and reported through a channel the
      application can observe.
- [ ] Supplied key + unreadable store → throws, and destroys nothing.
- [ ] A single unreadable document does not condemn the store — or, if Cabinet cannot yet support
      that, the limitation is documented and an upstream issue is raised.
- [ ] The failure is detected at a predictable point rather than wherever the first read lands.
- [ ] Decision recorded on each open question.
- [ ] Tests: wrong supplied key throws; derived-key mismatch recreates and reports; a recreated
      store is usable afterwards.
- [ ] README documents both behaviours under storage and encryption.

## Notes

- Raised by the author while reviewing [issue 48](48-exclude-store-from-os-backup.md): "how do we
  handle a failed decryption... this question should have existed before we knew this anyway."
  Correct — 48 supplied a likely trigger, not the problem.
- Suggested milestone **v1.0**. Unlike 48 this is not documentation: an unhandled
  `CryptographicException` out of an HTTP call is a defect, and "the app can never start again"
  is not an acceptable response to a restored backup. The v1.0 list is deliberately short, so
  this is a proposal rather than an assumption — the minimum that must ship is the typed
  exception and the boundary catch, which is small.
- Sequencing: settle [32](32-default-encryption-key.md)'s derivation first. It changes how often
  this code path is reached, which changes how much of it is worth building.
