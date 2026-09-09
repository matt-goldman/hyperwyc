# Issue 70 — Move Request and Response Bodies to Cabinet Attachments

## Summary

Bodies are `byte[]` on `Envelope.RequestBody`, `CachedResponse.Body` and `DeliveryOutcome.Body`, which System.Text.Json writes as base64 **inside the record**. Cabinet's `RecordSet` stores the whole set as one document and rewrites it on every single-record change, so every cached body is re-serialised, re-encrypted and re-written on every subsequent write, at a 33% base64 premium.

Cabinet 2.0 can read an attachment back. Move the bytes out.

## Status

✅ **Done.** 2026-09-09, on the `upgrade-cabinet` branch. Filed the same day, out of
[issue 52](52-store-rewrites-whole-set-per-write.md) and
[ADR 0008](../../docs/decisions/0008-shrink-what-you-store.md).

## Why now and not at issue 25

[Issue 25](25-binary-request-response-bodies.md) considered exactly this and rejected it, on three specific blockers. Cabinet 2.0 clears all three:

| Blocker recorded in 25                                                                                              | State in Cabinet 2.0                                                                                                                                               |
| ------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| `SaveAsync(id, data, attachments)` writes blobs but nothing can read one back                                       | `OpenAttachmentAsync(recordId, name)` returns a `Stream?`                                                                                                          |
| `FileAttachment` as a record property throws `InvalidOperationException: Timeouts are not supported on this stream` | Records carry `AttachmentInfo` (name, content type, length); `FileAttachment` is now write-side only and throws a documented `NotSupportedException` if serialised |
| A per-envelope attachment would orphan on delete, because `RemoveAsync` only re-saves the set                       | `RemoveAsync` deletes the record's attachments; `CompactAttachmentsAsync` reclaims orphans                                                                         |

25's closing line was "Revisit when Cabinet can read an attachment back." It can.

## Why it matters

**This is what actually drives [52](52-store-rewrites-whole-set-per-write.md).** The O(n²) there is real, but n's weight is bytes rather than records, and bodies are essentially all of the bytes. An envelope without a body is a few hundred bytes of URL, method, headers and flags; an envelope with one is up to ~683 KB of base64 against a 512 KB `MaxCachedResponseBodyBytes` cap. Taking the bodies out is what makes the rewritten document small enough for the amplification to stop mattering, without asking Cabinet to change how it writes.

**Attachments are written once.** A blob is a separate encrypted file, written when the body is captured and never touched again by a subsequent record save. It is also never decrypted on a record load, so `GetPendingOutboxAsync` — which loads every envelope to filter and sort them — stops paying for bodies it does not read.

## Shape

Ordering is the part to get right, and `AddAttachmentAsync` is a separate call from the record save, so Hyperwyc controls it: **write the blob first, then the record that references it.** A crash between the two leaves an orphaned blob and no record, which is harmless and reclaimable. The reverse order leaves a queued write whose body is missing, which would replay wrong — the exact failure Hyperwyc exists to prevent.

Reads are the mirror: `Envelope` carries `AttachmentInfo`, and the bytes are fetched only on the path that needs them — the replay path for a request body, the serve path for a cached response.

## Lifecycle Hyperwyc has to take on

Cabinet cleans up on `RemoveAsync`. Hyperwyc mostly does not remove.

- **`InvalidateCacheForPrefixAsync`** nulls `Response` and calls `UpdateAsync`, so the record survives and its blob orphans. It has to delete the blob explicitly.
- **`CabinetStore.ResetAsync`** deletes top-level files under `records`, `attachments` and `index`. 2.0 puts attachments in `attachments/{hash(recordId)}/`, so a top-level file sweep now misses them entirely. It has to delete the directory tree. Harmless today only because Hyperwyc writes no attachments; this item is what makes it matter.
- **Dead-lettered envelopes** keep their bodies indefinitely, which is correct — the body is the thing the application needs to inspect or resubmit — but it interacts with [67](../67-configurable-response-retention.md) and [24](../24-v1-dead-letter-management.md).
- **`CompactAttachmentsAsync`** exists for orphans and opens every attachment directory, so it belongs on a maintenance path, not on load.

## What was built

`CabinetStore` writes the three bodies as attachments named `request`, `response` and `outcome`, then stores a **copy of the envelope with the bodies nulled**. Reads hydrate a copy back.

The copy is the part that is easy to get wrong. `RecordSet` hands out the instances it holds in memory and writes those same instances back on the next `SaveAllAsync`, so hydrating one in place would put the bodies straight back into the document this exists to keep them out of — not on the write that hydrated it, but on some later, unrelated write. `WithBodies` builds a new `Envelope` in both directions, and nothing mutates a stored instance's bodies.

Hydration is deliberately asymmetric. `GetCachedResponseAsync` filters first and hydrates the single match, so loading the record set decrypts no bodies at all. `GetPendingOutboxAsync` hydrates everything it returns, which is correct because the outbox is bounded by what is queued and every envelope in it is about to be replayed — where the cache is unbounded and exactly one entry of it gets served.

The stale sweep runs on the update path only, off one manifest read, and is skipped entirely when adding. A body going from present to absent takes its blob with it; an orphan under a reused id is `CompactAttachmentsAsync`'s job, which is the method's stated purpose.

## Acceptance Criteria

- [x] Request bodies, cached response bodies and outcome bodies are stored as Cabinet attachments rather than base64 inside the record.
- [x] The blob is committed before the record that references it, on every write path.
- [x] Bodies are fetched only where they are used; a record load does not decrypt them.
- [x] ~~`InvalidateCacheForPrefixAsync` deletes the blob it orphans.~~ **Not needed.** [68](68-cache-invalidation-leaves-tombstones.md) landed first and made invalidation remove the record, and Cabinet's `RemoveAsync` deletes a record's attachments — so the bytes go with the entry and no compensating cleanup was written. Doing 68 first is what deleted this requirement rather than satisfying it.
- [x] `ResetAsync` clears the attachment tree, not just top-level files.
- [x] ~~A benchmark showing single-record write cost does not grow with store size~~ — inherited from [52](52-store-rewrites-whole-set-per-write.md), **moved out to [71](../71-store-benchmarks-and-alternative-implementations.md)**. It was never this item's criterion: it measures the store, not this change, and it wants a harness rather than a test. `CachedBody_DoesNotLandInTheRecordDocument` covers what belongs here, which is that the bodies left the document.
- [x] The persisted shape changes, so a developer with a live store needs a reinstall — see 25's note on exactly this, and [49](49-unreadable-store-recovery.md), which should make it a non-event.

## How it is held

- `CachedBody_DoesNotLandInTheRecordDocument` — caches a 256 KB body and asserts `records/Envelope.dat` stays under 8 KB, then asserts the bytes came back after a reopen, so it cannot pass by losing them. Fails at ~350 KB against the previous implementation; verified by reverting, not assumed.
- `EmptyBody_StaysDistinctFromNoBody` — `byte[0]` and `null` mean different things, and a zero-length attachment is how the difference survives. Easy to lose to a `Length == 0` shortcut.
- `UpsertAsync_BodyThatBecomesAbsent_TakesItsBlobWithIt` — the stale sweep.
- `ResetAsync_ClearsTheAttachmentTree`.
- `FullEnvelopeGraph_SurvivesReopeningTheStore`, written for [53](53-aot-json-serialization.md), turned out to cover this too: it reopens the store and asserts all three bodies, so it now exercises the attachment round trip end to end without being changed.

## Notes

- **Not a correctness fix.** The bytes were already right; 25 established that. This is where they live.
- **[52](52-store-rewrites-whole-set-per-write.md) is not fully closed by this.** The amplification is now small rather than absent: the document still gets rewritten per write, it just holds metadata. [42](../42-cache-eviction.md) bounds the record count and is the other half.
- **The prediction is still a prediction.** A store of 100 cached 500 KB responses should go from rewriting tens of megabytes per cache write to under a megabyte. The mechanism for that is in place and asserted; the number is not measured, and lives in [71](../71-store-benchmarks-and-alternative-implementations.md) with the rest of the store's numbers rather than alone here.
- Cabinet buffers attachment content in memory on save and read — its own docs say a streaming path would need `IEncryptionProvider` to work in chunks. Hyperwyc buffers bodies anyway, so this changes nothing today, but it is the same wall that streaming bodies would hit.
