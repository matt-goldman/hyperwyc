# Issue 25 — Binary Request and Response Bodies

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

- [ ] `Envelope.RequestBody` and `CachedResponse.Body` are stored as `byte[]?` (or equivalent) rather than `string?`.
- [ ] Round-trip tests cover: JSON request, binary request (e.g. PNG bytes), JSON response, binary response, gzip-encoded response read as bytes.
- [ ] Replayed binary requests preserve byte-for-byte equality.
- [ ] Cached binary responses are served with the original `Content-Type` and `Content-Encoding`.
- [ ] `CabinetSyncStore` schema migration handles existing string-encoded rows or is documented as a breaking change requiring a store reset.
- [ ] `InMemorySyncStore` updated to the new shape.

## Notes

- This is a breaking change to the persisted shape of `Envelope` and `CachedResponse`. Either a schema migration in `Hyperwyc.Cabinet` or a documented one-time `ResetStoreAsync` on upgrade is required.
- Consider whether to offer a convenience string view (e.g. `Envelope.GetRequestBodyAsString()`) for callers/diagnostics that previously relied on string content.
- Streaming uploads/downloads (chunked, indeterminate length) are explicitly out of scope for this issue — buffered byte arrays only. Streaming can be a follow-up if demand emerges.
- **Decision:** Migration and breaking-change mitigation are NOT required. Current version is v <1, and this is in scope for v1. As this library is preview, breaking changes are to be expected and support is not required.
