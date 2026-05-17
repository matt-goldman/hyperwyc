# Issue 26 — [v2.0, under consideration] Typed Response Shaping for Offline Reads

## Summary

Provide a way for Hyperwyc to return a *deserialisable* body (e.g. `"[]"`, `"{}"`, or a caller-supplied default) on the transparent-offline read path, rather than always returning an empty body that breaks `GetFromJsonAsync<T>` and similar typed-deserialisation calls.

## Status

**Under consideration for v2.0.** This issue captures the problem and the candidate approaches; no commitment to ship.

## Background

With `OfflineResponsePolicy.Transparent` and no cached response, Hyperwyc returns `200 OK` with an empty body. Callers using:

```csharp
var results = await httpClient.GetFromJsonAsync<List<MyDto>>("/feature/get");
```

will hit a `JsonException` rather than the empty-collection result the service-worker-inspired model promises. The current mitigation, documented in the README, is for applications to adopt a response envelope/result pattern (e.g. `ApiResponse<T> { Ok, Data, Error }`) so that an empty body deserialises to `null` or a default. That works but pushes a contract change onto every API.

This issue tracks doing better — having Hyperwyc itself emit a payload that round-trips through a typed deserialiser.

## Candidate Approaches

### Option A — Per-route empty body in route policy (preferred, possibly v1.x)
Extend per-route policies (issue #22) with an `EmptyOfflineBody` (string or `Func<HttpRequestMessage, string>`):

```csharp
.For("/api/notes", route =>
{
    route.EmptyOfflineBody = "[]";
    route.EmptyOfflineContentType = "application/json";
})
```

No reflection, no compile-time magic, reuses infrastructure already planned for v1.0. Likely the right pragmatic answer.

### Option B — Source generator
Inspect call sites of `GetFromJsonAsync<T>`, `ReadFromJsonAsync<T>`, typed Refit/RestEase interfaces, etc., and emit per-type default-payload tables that Hyperwyc consults at runtime. No runtime reflection, zero per-route configuration, but a large infrastructure investment for what is ultimately a UX nicety.

### Why not runtime type inference?
Reflection-based discovery of the caller's target type (walking the call stack, intercepting via `HttpClient` extensions, etc.) is rejected: brittle, AOT-hostile, and a layering violation. Source generator or per-route policy only.

## Acceptance Criteria (if/when accepted)

- [ ] Decide between Option A, Option B, or both.
- [ ] If Option A: per-route `EmptyOfflineBody` + content type, returned from `HandleOfflineReadAsync` when no cache exists. Unit tests cover JSON empty array, JSON empty object, custom shape, and content-type propagation.
- [ ] If Option B: source generator emits per-type defaults from discovered call sites; runtime resolves them in the offline read path; tests cover discovery, AOT compatibility, and trimming.

## Notes

- The sample MAUI app (issue #19) should explicitly demonstrate the response-envelope pattern as the *current* mitigation, so that anyone reading the sample sees the intended idiom rather than discovering the empty-body problem the hard way.
- If Option A lands as part of #22, this issue may close as duplicate, leaving only the source generator question (which is firmly "nice to have, low priority").
