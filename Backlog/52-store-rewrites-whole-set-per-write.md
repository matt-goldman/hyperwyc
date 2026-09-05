# Issue 52 — Every Cache Write Rewrites the Entire Store

## Summary

`CabinetStore` persists through Cabinet's `RecordSet<Envelope>`, and every single-record
change calls `SaveAllAsync` — serialising, encrypting and rewriting the whole record set. One
cached response therefore costs O(total records) to store, and filling a cache costs O(n²).

## Status

⬜ Open. Filed 2026-08-25, out of the investigation in
[issue 51](Done/51-cabinet-store-not-thread-safe.md).

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
([issue 42](42-cache-eviction.md)) exists, so the file this rewrites per request can reach tens
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
3. **Bound the store so n stays small.** [Issue 42](42-cache-eviction.md) does this anyway for
   other reasons, and would cap the damage without addressing the cause.

1 is the right answer; 3 is happening regardless; 2 is a fallback if 1 is not available.

## Related

- [Issue 51](Done/51-cabinet-store-not-thread-safe.md) — the crash this was found under, and the
  concurrency window this widens.
- [Issue 42](42-cache-eviction.md) — bounding the cache limits how bad this gets.
- [Issue 48](48-exclude-store-from-os-backup.md) — the same unbounded growth is what threatens
  Android's 25 MB backup quota.
- [Issue 49](49-unreadable-store-recovery.md) — wants `ResetAsync` to clear files rather than
  enumerate records, which would also fix the reset-is-N-saves case here.

## Acceptance Criteria

- [x] Establish what `RecordSet` actually does for `AddAsync`, `UpdateAsync` and `RemoveAsync` —
      all three call `SaveAllAsync`; confirmed in Cabinet's source.
- [ ] Decide between the options above, upstream question settled first.
- [ ] A benchmark or test showing single-record write cost does not grow with store size.
- [ ] `ResetAsync` clears in one operation rather than one save per record.

## Notes

- Not a regression and not urgent at sample scale, but it is the kind of thing that is invisible
  in testing and obvious in production, because it only shows up once a real user has been
  running the app for a while.
- Suggested milestone **v1.2**, unless [42](42-cache-eviction.md) lands first and makes it moot
  in practice.
