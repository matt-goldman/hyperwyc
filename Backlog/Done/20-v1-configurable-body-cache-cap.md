# Issue 20 — [v1.0] Make `MaxCachedResponseBodyBytes` Publicly Configurable

## Summary

Expose `MaxCachedResponseBodyBytes` as a first-class configurable option — application-wide on `HyperwycOptions`, and **overridable per route** on `RoutePolicy`.

## Background

In v0.1, `MaxCachedResponseBodyBytes` defaulted to 512 KB and was already wired through `HyperwycOptions` internally (see issue #17). This issue makes that limit visible, validated and documented, and adds the per-route override.

**The per-route override is the substance of the item, not a nicety.** One number does not fit a whole API. Raising the global cap for the single endpoint that returns a document raises it for every endpoint — and on a store that nothing evicts from ([42](../42-cache-eviction.md)), that headroom is what eventually fills the device. The choice a consumer actually wants to make is "this route's payloads are large, the rest are not", which is a per-route statement.

## Acceptance Criteria

- [x] `HyperwycOptions.MaxCachedResponseBodyBytes` is documented in XML comments and in the docs.
- [x] `RoutePolicy.MaxCachedResponseBodyBytes` overrides it for a matched route; `null` — the default — inherits the application-wide value.
- [x] Changing the value in `AddHyperwyc()` options takes effect without code changes elsewhere.
- [x] Value of `0` means "cache no bodies", not "no limit". Decided — see Decisions.
- [x] Negative values throw `ArgumentOutOfRangeException` at configuration time, on the options and on every registered policy, naming the route that set it.
- [x] Unit tests: a custom global limit is respected; a route cap raises and lowers the global one; an unset route cap inherits; a route cap of `0` caches nothing; each negative case throws at registration.

## Decisions

**`0` means "cache no bodies", and there is no unlimited sentinel.** The alternatives considered were `0` for unlimited and `-1` for unlimited. Both were rejected: `0` for unlimited would contradict `MaxOutcomeBodyBytes`, two properties below it in the same class, which already documents "set to zero to capture no bodies at all" — one value, two meanings, in one options object. `-1` is the better sentinel of the two and has `Timeout.Infinite` behind it, but it buys nothing here, because a body is a `byte[]` and `int.MaxValue` therefore already exceeds anything that could be cached. Declining the sentinel also keeps validation as a flat "negative throws", with no exception-to-the-exception, and keeps the per-route property to two special values (`null` inherits, `0` stores nothing) rather than three.

**Validation lives in `AddCoreServices`, after the `configure` delegate returns.** A property setter can only see the value in front of it, and the route policies are values the consumer composed separately and handed over — the first moment all of them exist is once configuration has finished. It throws rather than clamping: a negative cap says "cache nothing" through a value that reads as a size, and silently treating it as `0` would leave a consumer a long way from the cause.

**The cap is inherited when unset, unlike `Ttl`.** Deliberately the opposite decision to issue #29. A TTL has no safe fallback — two sources for one validity bound is exactly what #29 was — whereas a size cap has exactly one, the application-wide number the consumer already chose. Making it concrete on every policy would mean restating 512 KB on each route that does not care about it.

## Notes

- The original statement that "the underlying code change is minimal" was wrong, which the later `UPDATE` line on this item recorded. Honouring that update is what took this from a documentation tidy to a feature.
- A gap was suspected in the cap's enforcement and turned out not to exist: the check reads `Content.Headers.ContentLength`, which is `null` on a chunked response and would read as `0`, but the body is buffered before the length is read, so the length is computed from the buffer. That ordering is load-bearing and not visible from the two lines involved, so `ResponseCacheReadTests.OversizedBodyWithNoContentLength_IsStillNotCached` now pins it.
