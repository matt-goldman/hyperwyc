# Issue 43 — `Vary` Is Not Honoured; the Cache Key Is the URL Alone

## Summary

`ISyncStore.GetCachedResponseAsync(url)` matches on the request URL and nothing else. A server
that varies its response by request header — content negotiation being the common case — will
have one variant served in place of another.

## The problem

```csharp
all.FirstOrDefault(e => e.Url == url && e.Response is not null && !e.IsDeadLettered);
```

That is the whole matching rule. `Vary` is stored with the response headers and never read.

Concretely, with a localised catalogue:

1. The app requests `/products` with `Accept-Language: en-GB`. The response comes back with
   `Vary: Accept-Language` and is cached.
2. The user switches language. The app requests `/products` with `Accept-Language: fr-FR`.
3. Hyperwyc serves the cached English catalogue.

Nothing indicates anything went wrong. It looks like a server bug, and it will be reported as
one.

`Accept` is the other common trigger — an endpoint serving both JSON and CSV, or versioned media
types like `application/vnd.example.v2+json`, which is a widespread API-versioning convention.
`Accept-Encoding` matters too once bodies are stored as bytes
([issue 25](Done/25-binary-request-response-bodies.md)), since a gzip-encoded body served to a client
that did not ask for gzip is not merely wrong, it is unreadable.

## Prior art

The Cache API respects `Vary` by default: `cache.match(request)` compares the named request
headers, and `ignoreVary: true` is an explicit opt-out for cases where the developer knows
better. So the browser's default is the safe one and the shortcut is opt-in.

Hyperwyc currently has only the shortcut.

## Behaviour

When caching a response that carries `Vary`, record the request-header values it names alongside
the entry. When matching, a cached entry is a hit only if those recorded values equal the current
request's.

- `Vary: *` means never reusable — do not cache at all.
- A response with no `Vary` matches on URL as it does today, so the common case is unchanged.
- Multiple variants of the same URL must be able to coexist in the store, which the current
  "first envelope with this URL" lookup cannot express.

That last point is the substantive change: the store's cache lookup is currently
`(url) -> entry`, and it needs to become `(url, variant) -> entry`. It touches `ISyncStore`, both
implementations, and the invalidation path, which today clears every entry under a URL prefix and
would now be clearing several variants at once — which happens to be correct.

## Open Questions

1. **How is the variant key represented?** A normalised, ordered concatenation of the named
   headers and their values is simple and readable in a diagnostics view. A hash is shorter and
   avoids unbounded key length, at the cost of being opaque when debugging.
2. **Does this interact badly with eviction ([issue 42](42-cache-eviction.md))?** A URL with
   several variants occupies several entries. Worth confirming that a chatty content-negotiating
   client cannot fill the cache with near-duplicates of one resource.
3. **Should an unrecognised or absurd `Vary` be treated as `Vary: *`?** Erring toward not caching
   is the safe reading, and matches how caches generally treat directives they cannot honour.

## Acceptance Criteria

- [ ] `Vary` request-header values are recorded with a cached response.
- [ ] A cached entry is served only when the current request's values for those headers match.
- [ ] `Vary: *` responses are not cached.
- [ ] Responses without `Vary` behave exactly as today.
- [ ] Several variants of one URL can coexist in the store.
- [ ] Write-triggered invalidation removes all variants under the matched prefix.
- [ ] Decisions recorded on the open questions above.
- [ ] Unit test: two requests differing only in a `Vary`-named header get different responses.
- [ ] Unit test: a `Vary: *` response is returned to the caller but not cached.
- [ ] Unit test: the no-`Vary` path is unchanged.
- [ ] `CabinetSyncStore` and `InMemorySyncStore` both implement variant matching.

## Notes

- Found while auditing Hyperwyc against Service Worker and Workbox for gaps.
- The failure mode is silent and looks like a server fault, which is what makes it worth fixing
  before there are users rather than after a confusing bug report.
- Best sequenced with [issue 25](Done/25-binary-request-response-bodies.md): both change how a cached
  entry is keyed and stored, and both touch every `ISyncStore` implementation.
