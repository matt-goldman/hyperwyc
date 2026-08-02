# Issue 30 — Sensitive Headers Are Persisted

## Summary

`Envelope.ForRequest` persists every request header verbatim, including `Authorization`
and `Cookie`. TECHNICAL_PLAN §9 states that sensitive headers are excluded by default.
The documentation describes a protection that does not exist.

## Background

```csharp
var headers = FlattenHeaders(request.Headers);
```

There is no filter. Any queued write or cached read stores the bearer token that was on
the request at the time — into Cabinet's on-disk store for `CabinetSyncStore`. Cabinet
encrypts at rest, which mitigates but does not remove the exposure: tokens outlive their
useful life in the store, are replayed verbatim on flush, and appear in any diagnostics
surface built for issue #23.

Replaying a stale token is also a functional problem, not only a privacy one. The
documented handler ordering (`HyperwycHandler` before the auth handler) exists precisely
so that replayed requests pick up a *fresh* token — but a persisted `Authorization` header
is re-applied by `SyncOrchestrator.BuildRequest`, and the orchestrator sends through a bare
transport handler that has no auth handler in it at all. So the stale header is what
actually goes on the wire during a flush.

## Behaviour

- Maintain a default deny-list of headers excluded from persisted envelopes:
  `Authorization`, `Proxy-Authorization`, `Cookie`, `Set-Cookie`.
- Make the list configurable via `HyperwycOptions` — additive, and with an opt-out for
  developers who deliberately want full request persistence (TECHNICAL_PLAN §9 already
  promises this flag).
- Excluded headers are dropped at envelope-construction time, not at replay time, so they
  never reach the store.
- Document that replayed requests acquire auth from the pipeline, and confirm the
  orchestrator's transport can pick up whatever the app's auth handler provides — see
  Notes.

## Acceptance Criteria

- [ ] Default deny-list applied in `Envelope.ForRequest` (and therefore `ForCachedResponse`).
- [ ] Deny-list is configurable and extendable via `HyperwycOptions`.
- [ ] Opt-in flag restores full header persistence.
- [ ] Unit tests assert `Authorization` is absent from a persisted envelope by default, present when opted in, and that a custom deny-list entry is honoured.
- [ ] TECHNICAL_PLAN §9 matches the shipped behaviour.

## Notes

- Discovered while reconciling the docs against the code; not previously tracked.
- **Open design question:** `SyncOrchestrator` sends replays through a bare
  `HttpClientHandler` constructed in `AddHyperwyc`, which bypasses the app's auth handler
  entirely. Dropping the persisted `Authorization` header therefore makes replayed
  requests unauthenticated unless the orchestrator is given a transport that includes the
  auth handler. Resolving that is part of this issue and may be the larger half of it.
