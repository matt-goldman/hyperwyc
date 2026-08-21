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

## Revised thinking: this was over-engineered

Still under consideration, but the shape has changed. Both original options were reaching for
more machinery than the problem needs.

The original framing was partly a preference for the result pattern over `null` — which the issue
itself already identifies as Hyperwyc being opinionated. Preferring a typed empty body over
`null` is the same opinion wearing a different hat.

### The measured behaviour

`GetFromJsonAsync<T>` against .NET 10, with the response body varied:

| Body | `GetFromJsonAsync<Product>` | `GetFromJsonAsync<List<Product>>` |
|---|---|---|
| *(empty — what Hyperwyc returns today)* | **throws `JsonException`** | **throws `JsonException`** |
| `null` | returns `null` | returns `null` |
| `[]` | — | empty list |

The important row is the first. **An empty body throws for a single object too**, not only for a
collection. So "do nothing, callers already handle `null`" does not work: a caller who is
perfectly prepared for `null` still gets an exception, because an empty body is not `null` — it
is not JSON at all.

### Three layers, not two

**Layer 0 — return `null` instead of nothing. ✅ Shipped.** Free, no configuration, no per-route
anything. Emitting the four characters `null` as the synthetic body instead of an empty one makes
`GetFromJsonAsync<T>` return `null` cleanly for objects *and* collections. Callers handle `null`,
which they were going to have to do anyway. This alone removed the sharp edge that motivated the
whole issue.

**No `Content-Type` is set on it.** The first cut asserted `application/json`, which was the one
part of the change that made a claim about someone else's API — wrong for SOAP, XML, protobuf or
anything else a route might serve. It turns out to be unnecessary: `GetFromJsonAsync<T>` does not
inspect the content type, verified against .NET 10 with the header absent, `text/plain` and
`application/xml`. So the body is four bytes that happen to be valid JSON, and Hyperwyc says
nothing about what they are. For a non-JSON consumer this is no worse than the empty body it
replaces — an XML parser rejects both.

Applied to all three synthetic responses — `Offline`, `CacheMiss`, and the `202` for a queued
write. The last was not in the original framing but has the identical defect: a caller reading
back a created resource with `ReadFromJsonAsync<T>` was getting an exception where `null` is the
honest answer, because there is no created resource yet.

**Layer 1 — a `ReturnsCollection` flag per route.** A collection deserialised from `null` is
`null`, so a caller doing `.Count` still faults. Knowing a route returns a collection lets
Hyperwyc emit `[]` instead, and the deserialiser does the rest. One boolean, no body strings, no
content-type wrangling — considerably lighter than the `EmptyOfflineBody` string originally
proposed, and it covers the case that actually bites.

**Layer 2 — the source generator.** Unchanged, and still firmly "nice to have, low priority".

### What this changes

Layer 0 was implemented separately from this issue, as it should have been: it is not "typed
response shaping" so much as "stop emitting something that is not JSON", and it is defensible on
its own merits without any decision about layers 1 and 2. Covered by
`SyntheticResponseBodyTests`, which exercise the real `GetFromJsonAsync<T>` extension methods
rather than asserting on a body string — the property worth protecting is that a caller's
ordinary deserialisation does not throw.

**What remains under consideration is therefore only layers 1 and 2.** The issue is much smaller
than it was: the sharp edge is gone, and what is left is the convenience of `[]` over `null` for
collections.

Layer 1 folds naturally into per-route policies ([issue 22](22-v1-per-route-policies.md)) as a
boolean rather than the free-form `EmptyOfflineBody` that item currently anticipates.

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

- [ ] Decide between the layers above. Layer 0 is close to unconditional; Layer 1 is a flag on
      per-route policy; Layer 2 remains speculative.
- [ ] If Option A: per-route `EmptyOfflineBody` + content type, returned from `HandleOfflineReadAsync` when no cache exists. Unit tests cover JSON empty array, JSON empty object, custom shape, and content-type propagation.
- [ ] If Option B: source generator emits per-type defaults from discovered call sites; runtime resolves them in the offline read path; tests cover discovery, AOT compatibility, and trimming.

## Notes

- The sample MAUI app (issue #19) should explicitly demonstrate the response-envelope pattern as the *current* mitigation, so that anyone reading the sample sees the intended idiom rather than discovering the empty-body problem the hard way.
- If Option A lands as part of #22, this issue may close as duplicate, leaving only the source generator question (which is firmly "nice to have, low priority").
