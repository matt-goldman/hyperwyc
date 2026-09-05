# Issue 25 — Binary Request and Response Bodies

## Status

✅ **Done.** 2026-08-25.

## Summary

Support binary HTTP request and response payloads end-to-end through Hyperwyc's queue and cache. Today both paths read content via `ReadAsStringAsync` and persist it as a string, which corrupts non-text payloads and silently breaks file uploads, image downloads, protobuf, gRPC-over-HTTP, gzip-encoded responses read as bytes, and similar workloads.

## Background

`Envelope.ForRequest` and `Envelope.ForCachedResponse` both call `Content.ReadAsStringAsync()` and store the result on string-typed properties (`Envelope.RequestBody`, `CachedResponse.Body`). This was a deliberate v0.1 scoping decision but it leaves a meaningful correctness gap for anything beyond JSON/text APIs. The README now explicitly calls this out as a current limitation; this issue tracks closing it.

## Behaviour

- Persist request and response bodies as raw bytes, not strings.
- Preserve `Content-Type` and `Content-Encoding` headers verbatim so the replay/serve path can reconstruct an equivalent `HttpContent`.
- When rehydrating, use `ByteArrayContent` (or `StreamContent` over a pooled buffer) rather than `StringContent`, and re-apply the original content headers.
- Decide a sensible default for the existing `MaxCachedResponseBodyBytes` cap given binary payloads tend to be larger; the cap itself stays applicable.

## Acceptance Criteria

- [x] `Envelope.RequestBody` and `CachedResponse.Body` are stored as `byte[]?` rather than `string?`. `SyncEvent.RequestBody` followed, since a string view of a binary body is the corruption this issue exists to fix.
- [x] Round-trip tests cover all five, in `BinaryBodyTests`. Four of the eight fail against a string round-trip; the JSON ones pass either way, which is the point.
- [x] Replayed binary requests preserve byte-for-byte equality.
- [x] Cached binary responses are served with the original `Content-Type` and `Content-Encoding` — the gzip test decompresses what it gets back.
- [x] ~~`CabinetSyncStore` schema migration handles existing string-encoded rows or is documented as a breaking change requiring a store reset.~~ **Not required** — nothing is released, so the persisted shape is free to change. Just change it.
- [x] `InMemorySyncStore` needed no change — it stores `Envelope` instances, so the shape is its business only through the type. `CabinetSyncStore` needed none either, but base64 round-tripping through System.Text.Json is now pinned by two tests rather than assumed.

## What was built

`ReadAsByteArrayAsync` on both capture paths, `ByteArrayContent` on both rehydration paths, and
`byte[]?` on `Envelope.RequestBody`, `CachedResponse.Body` and `SyncEvent.RequestBody`.

`ByteArrayContent` matters for a second reason beyond fidelity: unlike `StringContent` it stamps
no `Content-Type` of its own, so the captured headers are the only source of the media type. A
`StringContent` default was exactly what made every replayed JSON write go out as `text/plain`
and come back `415` — fixed separately just before this, and this change removes the trap rather
than working around it.

### Text accessors

`GetRequestBodyAsText()` on `Envelope` and `SyncEvent`, `GetBodyAsText()` on `CachedResponse`,
matching the one `SyncOutcome` already had. UTF-8, no consultation of the captured
`Content-Type`. They exist for diagnostics and for callers who know their route is textual;
nothing inside Hyperwyc calls them, because nothing inside Hyperwyc is entitled to assume a body
is text.

### The cap default: unchanged, deliberately

The issue asked for a decision on `MaxCachedResponseBodyBytes` given binary payloads are larger.
It stays at 512 KB. Raising it invites unbounded store growth while cache eviction
([issue 42](../42-cache-eviction.md)) does not exist and every write rewrites the whole record set
([issue 52](../52-store-rewrites-whole-set-per-write.md)); a consumer who wants to cache large
binaries can raise it themselves. Adding headroom nobody asked for, against a store that has no
eviction, is the move [ADR 0004](../../docs/decisions/0004-default-to-removal.md) exists to stop.

## Cabinet attachments were considered, and cannot be used yet

Bytes land in the record as base64, which System.Text.Json does automatically for `byte[]`. That
is 33% bloat on a store where every write rewrites the whole record set
([issue 52](../52-store-rewrites-whole-set-per-write.md)) — a 500 KB image becomes 667 KB
rewritten on every cache write. Cabinet has an attachments feature that would fix exactly this,
storing the body as a separate encrypted file instead. It was investigated and rejected for now.

There are two attachment patterns and neither works for this:

**`SaveAsync(id, data, attachments)`** writes encrypted files to `attachments/`, but Cabinet has
no way to read one back. `IOfflineStore` exposes `SaveAsync`, `LoadAsync<T>`, `DeleteAsync` and
`FindAsync`, and nothing in the library opens that directory except `DeleteAsync`'s cleanup.
Cabinet's own `SaveAsync_WithAttachments_ShouldSaveAttachmentFiles` asserts the file exists on
disk and that its bytes are encrypted; it never reads one back through the library.

**`FileAttachment` as a record property** — the pattern Cabinet's demo documents as "Cabinet
handles it automatically" — throws on serialisation, before any round-trip question arises:

```
InvalidOperationException: Timeouts are not supported on this stream.
```

`FileAttachment` wraps a `Stream` with no converter and no `[JsonIgnore]`, so System.Text.Json
walks the stream's public properties and `ReadTimeout` throws on a `MemoryStream`.

Both are Cabinet bugs on their own terms rather than gaps created by Hyperwyc's needs, and are
worth raising there. A third piece — per-record attachments with lifecycle cleanup through
`RecordSet` — genuinely would be reshaping Cabinet to suit Hyperwyc: `RecordSet` saves the whole
set as one document keyed on the type name, and `RemoveAsync` only re-saves the set, so a
per-envelope attachment would orphan on delete. That one should wait for a second consumer to
want it.

Revisit when Cabinet can read an attachment back. Until then this is a storage-location
optimisation, not a correctness gap — the bytes are already right.

## Notes

- This changes the persisted shape of `Envelope` and `CachedResponse`, and that costs nothing:
  the repository is private, nothing is published, and the only consumers are the sample and the
  tests. No migration, no documented reset, no compatibility shim. Change the shape and move on.
- Consider whether to offer a convenience string view (e.g. `Envelope.GetRequestBodyAsString()`) for callers/diagnostics that previously relied on string content.
- Streaming uploads/downloads (chunked, indeterminate length) are explicitly out of scope for this issue — buffered byte arrays only. Streaming can be a follow-up if demand emerges.
- **A stale store on a developer device is not the same as "no consumers".** This change was
  written, rolled back and restored. The rollback was triggered by
  `JsonException: The JSON value could not be converted to System.Byte[]. Path: $[0].Response.Body`
  with an inner `FormatException: Cannot decode JSON text that is not encoded as valid Base64`
  — which is not an HTTP failure at all. It is Cabinet deserialising a store written by the
  *previous* build, where `Body` was a JSON string rather than base64. The fix is to delete and
  reinstall the app.

  The reasoning that migration was unnecessary because nothing is released was right about other
  consumers and wrong about the one consumer that exists: the developer, with a live store,
  mid-debug. Any future change to the persisted shape — [22](22-v1-per-route-policies.md) will
  be one — needs the same reinstall, until
  [issue 49](49-unreadable-store-recovery.md) makes an unreadable store self-healing.
- **Decision:** Migration and breaking-change mitigation are NOT required. Current version is v <1, and this is in scope for v1. As this library is preview, breaking changes are to be expected and support is not required.
