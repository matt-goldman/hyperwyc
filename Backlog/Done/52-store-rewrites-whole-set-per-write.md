# Issue 52 — Every Cache Write Rewrites the Entire Store

## Summary

`CabinetStore` persists through Cabinet's `RecordSet<Envelope>`, and every single-record
change calls `SaveAllAsync` — serialising, encrypting and rewriting the whole record set. One
cached response therefore costs O(total records) to store, and filling a cache costs O(n²).

## Status

⛔ **Superseded.** 2026-09-09, by [70](70-move-bodies-to-cabinet-attachments.md) and
[42](../42-cache-eviction.md) under [ADR 0008](../../docs/decisions/0008-shrink-what-you-store.md).
Filed 2026-08-25, out of the investigation in
[issue 51](51-cabinet-store-not-thread-safe.md).

**Cabinet 2.0 does not change this.** Re-checked against 2.0's source on 2026-09-09, because the
upgrade was taken on the expectation that it would: `RecordSet.AddAsync`, `UpdateAsync` and
`RemoveAsync` still all end in `await SaveAllAsync(cancellationToken)`, and `SaveAllAsync` still
calls `_store.SaveAsync(_fileName, records)` with the whole list. 2.0's changes are attachments,
the storage layout for attachments, and commit ordering — none of which touch the record write
path. Option 1 below is still available and still not taken.

The diagnosis here holds; the fix it names does not. See "Option 1 is refused" below.

## Evidence

From the stack in 51:

```
Cabinet.Core.FileOfflineStore.SaveAsync<List<Envelope>>
Cabinet.Core.RecordSet<Envelope>.SaveAllAsync
Cabinet.Core.RecordSet<Envelope>.AddAsync        ← adding ONE envelope
```

**Confirmed against Cabinet's source**, not inferred: `AddAsync`, `UpdateAsync` and
`RemoveAsync` all end in `await SaveAllAsync(cancellationToken)`. That also confirms
`ResetAsync` — a `RemoveAsync` loop — is a burst of full-file writes rather than one.

Each of those writes is: serialise every envelope, AES-256-GCM encrypt the lot, write
`Envelope.dat.tmp`, `File.Move` it over `Envelope.dat`.

## Why it matters

**Cost grows with what is already cached.** The 200th cached response is 200× more expensive to
store than the first. Cached bodies are capped at 512 KB each
(`MaxCachedResponseBodyBytes`) and nothing caps the total until cache eviction
([issue 42](../42-cache-eviction.md)) exists, so the file this rewrites per request can reach tens
of megabytes.

**It widens every concurrency window.** This is what makes it more than a performance note. The
race fixed in 51 needed two saves to overlap; how likely that is depends directly on how long a
save takes, which grows monotonically as the cache fills. A store that is fine on day one gets
progressively easier to collide with, without a line of code changing.

**It is the best available explanation for 51's reliability change** — see that item's "What this
does not explain". The failure went from never to always with no diff that accounts for it, and
the author's instinct was that something had simply got slower. Store growth is a mechanism that
gets slower monotonically, resets when app data is cleared, and requires no code change at all.
Unproven, but it fits the shape of the report better than anything else considered.

**Mobile makes it worse.** Rewriting a large encrypted file on every read that gets cached is
battery, flash wear, and latency on the request path — the write is awaited before the response
is returned to the caller.

## Where the fix belongs

Partly upstream. `SaveAllAsync`-per-change is `RecordSet`'s design, not Hyperwyc's, so the real
fix is Cabinet supporting an append or per-document write. Options, roughly in order of appeal:

1. **Per-document storage in Cabinet.** One file per envelope, or an append-and-compact log.
   Removes the amplification entirely and makes single-record writes O(1).
2. **Batch or debounce writes in Hyperwyc.** Coalesce saves over a short window. Cheaper to
   build, but it trades durability for throughput — a queued write not yet flushed to disk when
   the process is killed is exactly the failure Hyperwyc exists to prevent, so this needs care
   and probably has to exclude outbox writes.
3. **Bound the store so n stays small.** [Issue 42](../42-cache-eviction.md) does this anyway for
   other reasons, and would cap the damage without addressing the cause.

1 is the right answer; 3 is happening regardless; 2 is a fallback if 1 is not available.

## Option 1 is refused, and Cabinet had already measured it

Added 2026-09-09. Cabinet's [performance principles](https://github.com/matt-goldman/Cabinet/blob/main/_docs/performance-principles.md) publish the comparison, for 5,000 records:

| Strategy | Files | Total |
|---|---|---|
| One file per record | 5,000 | ~10,000 ms |
| Single aggregate file | 1 | ~20 ms |

`SaveAllAsync`-per-change is not an oversight in `RecordSet`. File count is the thing Cabinet is built to minimise, and option 1 asks it to be ~500× slower at the case it was designed around, for one consumer's benefit. Written here as "the right answer" without having looked for the upstream reasoning, which existed and was published.

**The diagnosis survives; the prescription does not.** The O(n²) is arithmetic. What was wrong is the assumption that n means *records*: the term that dominates the rewritten document is **bytes**, and Hyperwyc's bytes are almost entirely base64-encoded request and response bodies inside the record — up to ~683 KB of base64 per cached response against a 512 KB cap. An envelope with no body is a few hundred bytes.

So the cost is a function of what Hyperwyc hands Cabinet, not of how Cabinet writes it, and it is ours to reduce. Cabinet 2.0 can read an attachment back, which is what [issue 25](25-binary-request-response-bodies.md) said to wait for, so the bodies can leave the record entirely — [issue 70](70-move-bodies-to-cabinet-attachments.md). With [42](../42-cache-eviction.md) bounding the record count independently, the two together are the resolution, and neither is this item.

Option 2 (batch or debounce in Hyperwyc) is also dropped, on its own original objection: it trades durability for throughput, and a queued write not yet flushed is the failure Hyperwyc exists to prevent.

The generalisation — look for the dependency's reasoning before proposing to redesign it — is [ADR 0008](../../docs/decisions/0008-shrink-what-you-store.md).

## Related

- [Issue 51](51-cabinet-store-not-thread-safe.md) — the crash this was found under, and the
  concurrency window this widens.
- [Issue 42](../42-cache-eviction.md) — bounding the cache limits how bad this gets.
- [Issue 48](48-exclude-store-from-os-backup.md) — the same unbounded growth is what threatens
  Android's 25 MB backup quota.
- [Issue 49](49-unreadable-store-recovery.md) — wants `ResetAsync` to clear files rather than
  enumerate records, which would also fix the reset-is-N-saves case here.
- [Issue 70](70-move-bodies-to-cabinet-attachments.md) — takes the bodies out of the rewritten
  document, which is the actual resolution.
- [Issue 25](25-binary-request-response-bodies.md) — put the bodies in the record as base64,
  having considered attachments and found Cabinet could not read one back yet.
- [ADR 0008](../../docs/decisions/0008-shrink-what-you-store.md) — the decision this item closes under.

## Acceptance Criteria

- [x] Establish what `RecordSet` actually does for `AddAsync`, `UpdateAsync` and `RemoveAsync` —
      all three call `SaveAllAsync`; confirmed in Cabinet's source, and re-confirmed against 2.0.
- [x] Decide between the options above, upstream question settled first. **All three refused.**
      1 is contradicted by Cabinet's own measurements, 2 by its own durability objection, and 3
      was never a fix. Resolution is [70](70-move-bodies-to-cabinet-attachments.md) plus
      [42](../42-cache-eviction.md).
- [ ] ~~A benchmark or test showing single-record write cost does not grow with store size.~~
      Moved to [70](70-move-bodies-to-cabinet-attachments.md), which is what has to prove it.
- [x] `ResetAsync` clears in one operation rather than one save per record — done under
      [49](49-unreadable-store-recovery.md), see the note below.

## Notes

- Not a regression and not urgent at sample scale, but it is the kind of thing that is invisible
  in testing and obvious in production, because it only shows up once a real user has been
  running the app for a while.
- Suggested milestone **v1.2**, unless [42](../42-cache-eviction.md) lands first and makes it moot
  in practice.
- The `ResetAsync` sub-case listed under [49](49-unreadable-store-recovery.md) is already
  resolved: `CabinetStore.ResetAsync` deletes the store's files directly rather than looping
  `RemoveAsync`, so it is no longer a burst of full-file writes. That leaves the ordinary write
  path as the whole of this item.
