# Issue 44 — No Cache Generation, so an App Upgrade Reads Yesterday's Shapes

## Summary

Cached response bodies survive application upgrades. When the shape of a DTO changes between
versions, Hyperwyc serves the old body to the new code, which deserialises it into the new type —
silently wrong, or throwing, depending on the change.

## The problem

The store persists what the server sent, indefinitely and without any notion of which version of
the application wrote it. Nothing invalidates on upgrade.

Consider a `Product` that gains a required `Currency` and renames `Price` to `UnitPrice`:

- v1.0 caches `{"id":1,"name":"...","price":25.87}`.
- v1.1 ships, reading `UnitPrice` and `Currency`.
- Offline, or within the TTL, the user sees a catalogue where every price is zero and every
  currency is null. No error, no signal, just wrong numbers on screen.

A rename that produces a missing value is the quiet case. A type change — a string id becoming an
integer, an enum gaining members, a nested object flattened — throws inside the caller's
deserialisation, presenting as a parse error against data the server never sent in that form.

The offline dimension makes it worse than an ordinary caching bug: the user may be somewhere with
no connectivity for days, so there is no refresh coming to heal it.

`ResetStoreAsync` would clear it, but nothing tells an application to call it, and calling it
unconditionally on every start throws away the offline capability the library exists to provide.

## Prior art

The canonical Service Worker pattern is versioned cache names — `caches.open('static-v3')` — with
the `activate` handler deleting every cache whose name is not current. Workbox generates a
revision per precached asset and does the same thing automatically.

The important part is not the naming convention. It is that **the cache is scoped to a generation,
and changing the generation discards the previous one wholesale**, with a defined moment for that
to happen. Hyperwyc has neither the scope nor the moment.

## Behaviour

A cache generation identifier, persisted with the store and compared at startup. When it differs
from the configured value, cached responses from the previous generation are discarded.

```csharp
services.AddHyperwyc(options => options.CacheGeneration = "v2");
```

Crucially, **only cached responses are discarded, never the outbox.** Undelivered writes are the
user's work, not a performance optimisation, and an app upgrade must not throw them away. That
distinction has to be exact for the same reason it does in
[issue 42](42-cache-eviction.md) — cache and outbox share one `Envelope` collection.

A queued write whose *body* was serialised by the previous version is a genuinely harder problem,
and is called out below rather than solved here.

## Open Questions

1. **Who supplies the generation?** An explicit string is predictable and lets a developer choose
   when to invalidate. Defaulting it to the entry assembly's version is more automatic but
   discards the cache on every patch release, including ones that change no contracts. Explicit,
   with the assembly version as a documented one-liner, is probably the better balance.
2. **What happens to a queued write serialised by an older version?** It cannot simply be dropped
   — it is undelivered user work — but replaying a body the current server may no longer accept is
   its own hazard. Options: replay it regardless and let the server reject it, which
   [issue 40](Done/40-surface-deferred-outcomes.md) now makes visible to the application; or surface
   it for the application to migrate or discard. Leaning toward the former, since it keeps
   Hyperwyc out of the business of understanding payloads.
3. **Is a generation mismatch worth an event?** "Your cache was cleared because the app updated"
   is useful in a diagnostics log and explains an otherwise mysterious cold start.

## Acceptance Criteria

- [ ] A cache generation is persisted with the store and compared at startup.
- [ ] A changed generation discards cached responses.
- [ ] A changed generation never discards outbox or dead-letter entries.
- [ ] Generation is configurable via `HyperwycOptions`, with the default documented.
- [ ] Decisions recorded on the open questions above.
- [ ] Unit test: cached responses are gone after a generation change.
- [ ] Unit test: queued writes survive a generation change.
- [ ] Unit test: an unchanged generation preserves the cache across restarts.
- [ ] README documents when to change the generation — contract changes, not every release.

## Notes

- Found while auditing Hyperwyc against Service Worker and Workbox for gaps.
- Best landed with [issue 25](Done/25-binary-request-response-bodies.md), which already changes the
  persisted shape. Doing them together is still worth it — one pass over the storage code rather
  than two — but not for migration reasons: nothing is released, so changing the shape is free.
- This is a foot-gun rather than a defect in shipped behaviour: nothing is wrong until a consumer
  ships their second version, at which point it is wrong in a way that is hard to attribute.
