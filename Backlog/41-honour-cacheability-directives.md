# Issue 41 — Honour the Server's Cacheability Directives

## Summary

Hyperwyc caches every successful GET response regardless of what the server said about
cacheability. `Cache-Control: no-store` is ignored, and the response is written to disk.

## The problem

Nothing in `src/` reads `Cache-Control`, `Pragma`, `Expires` or `Age`. `TtlStalenessEvaluator`
considers only `CachedAt` plus a configured TTL, so freshness is entirely a client-side opinion
and the server has no say in it.

The consequences run from wasteful to wrong:

| Directive | What it means | What Hyperwyc does |
|---|---|---|
| `no-store` | Do not persist this anywhere | Persists it, encrypted, to the device |
| `no-cache` | May store, but revalidate before reuse | Serves it from cache without asking |
| `private` | Not for shared caches | Immaterial here — Hyperwyc is a private cache — but worth confirming rather than assuming |
| `max-age` / `Expires` | The server's own freshness window | Ignored in favour of the configured TTL |

`no-store` is the serious one. An endpoint returning something the server has explicitly said not
to retain — a one-time token, a payment detail, a document under a retention policy — ends up in
`CabinetSyncStore` on the device, protected only by the default derived key
([issue 32](32-default-encryption-key.md)).

## Why this is Hyperwyc's problem, and why the Service Worker precedent does not excuse it

A Service Worker's Cache API also ignores `Cache-Control`; its cache is a separate store from the
HTTP cache and the developer is expected to be explicit about what goes in it. It would be easy
to read that as precedent.

It is not, because of what makes it defensible there: **a Service Worker caches only what you
opted in, route by route, with `registerRoute`.** The developer named the thing, so the server's
opinion is secondary to an explicit local decision.

Hyperwyc caches *every* GET by default. It inherited the stance without the precondition that
justified it. Per-route policies ([issue 22](22-v1-per-route-policies.md)) would let a developer
opt a route out, but that is not the same as respecting an instruction the server already sent.

Against [the scope test](../docs/decisions/README.md#the-standing-scope-test):

- Would the problem exist without Hyperwyc? **No** — `HttpClient` does not cache.
- Does it require anything of the consumer's API? **No** — the API already sends these headers.
- Can the application do it itself? **No** — caching happens inside the handler.
- Does it depend on what only Hyperwyc knows? **Yes** — it is Hyperwyc's cache.

## Behaviour

- **`no-store`** — never write the response to the store; return it to the caller untouched.
- **`no-cache`** — may be stored, but always treated as stale, so it is revalidated before reuse.
  Pairs naturally with conditional requests ([issue 46](46-conditional-requests.md)).
- **`max-age` / `Expires`** — decide precedence against `DefaultCacheTtl` and any per-route TTL.
  Suggest: the server's value wins when present, since it is the party that knows, with
  configuration as the fallback rather than the override. Needs deciding, not assuming.
- **`Vary`** is a separate concern — see [issue 43](43-honour-vary-header.md).

Directive parsing belongs behind `IStalenessEvaluator` or alongside it, so that an application
that wants purely TTL-based behaviour can still choose it.

## Open Questions

1. **Does a configured TTL override the server, or defer to it?** Deferring is more correct;
   overriding is what someone setting `CacheFirst(TimeSpan.FromDays(1))` probably expects. A
   possible answer: server directives cap the configured TTL but never extend it.
2. **Should `no-store` be honoured unconditionally, or be an option?** Unconditional is the
   defensible default. An escape hatch invites someone to disable the one directive that exists
   to prevent data being written down.
3. **How much of RFC 9111 is in scope?** Full HTTP caching semantics — `s-maxage`,
   `stale-while-revalidate`, `must-revalidate`, heuristic freshness from `Last-Modified` — is a
   large surface. Suggest handling the directives above and explicitly documenting the rest as
   not implemented, rather than implying full compliance.

## Acceptance Criteria

- [ ] A response with `Cache-Control: no-store` is returned to the caller and not written to the store.
- [ ] A response with `Cache-Control: no-cache` is stored but never served without revalidation.
- [ ] `max-age` and `Expires` participate in freshness, with the precedence rule documented.
- [ ] Behaviour is reachable through `IStalenessEvaluator` so purely TTL-based behaviour remains available.
- [ ] Decisions recorded on the open questions above.
- [ ] Unit test: `no-store` response is returned but absent from the store.
- [ ] Unit test: `no-cache` response is stored but a subsequent read does not serve it directly.
- [ ] Unit test: `max-age` shorter than the configured TTL wins.
- [ ] README and TECHNICAL_PLAN §2 state which directives are honoured and which are not.

## Notes

- Found while auditing Hyperwyc against Service Worker and Workbox for gaps.
- Highest-priority of that audit: it is on-by-default behaviour that contradicts an explicit
  server instruction and writes to durable storage. Everything else found was a growth,
  correctness or capability gap rather than a directive being disregarded.
- Related: [issue 46](46-conditional-requests.md) — `no-cache` is only cheap if revalidation is
  cheap, which is what conditional requests provide.
