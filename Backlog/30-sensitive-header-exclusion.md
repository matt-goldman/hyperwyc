# Issue 30 — Document That Caller-Set Headers Are Persisted

> **Reversed: this is now a documentation item, not a deny-list.** Stripping caller-set headers
> would violate the fidelity obligation in
> [ADR 0001](../docs/decisions/0001-idempotency-is-not-hyperwycs-remit.md) and break replay for
> anyone whose credential is still valid at replay time. See "Why a deny-list is the wrong
> answer" below.

> **[66](66-dead-letter-store-fails-the-scope-test.md) shortens the exposure this documents, for one of the two kinds.** If a delivered write is discarded rather than retained, an outbox entry's headers persist only while the write is *outstanding*, instead of indefinitely. Cache entries are unaffected — they still carry the request headers of the `GET` that populated them, for as long as the entry lives. Whatever this item ends up saying should distinguish the two.

## Summary

`Envelope.ForRequest` persists every request header verbatim, including `Authorization`
and `Cookie`. The since-removed TECHNICAL_PLAN claimed sensitive headers were excluded by default; they were not.
The documentation describes a protection that does not exist.

## Background

```csharp
var headers = FlattenHeaders(request.Headers);
```

There is no filter. Any queued write or cached read stores the bearer token that was on
the request at the time — into Cabinet's on-disk store for `CabinetStore`. Cabinet
encrypts at rest, which mitigates but does not remove the exposure: tokens outlive their
useful life in the store, are replayed verbatim on flush, and appear in any diagnostics
surface built for issue #23.

Replaying a stale token is also a functional problem, not only a privacy one. The
documented handler ordering (`HyperwycHandler` before the auth handler) exists precisely
so that replayed requests pick up a *fresh* token — but a persisted `Authorization` header
is re-applied by `OutboxProcessor.BuildRequest`, and the orchestrator sends through a bare
transport handler that has no auth handler in it at all. So the stale header is what
actually goes on the wire during a flush.

## Behaviour

- Maintain a default deny-list of headers excluded from persisted envelopes:
  `Authorization`, `Proxy-Authorization`, `Cookie`, `Set-Cookie`.
- Make the list configurable via `HyperwycOptions` — additive, and with an opt-out for
  developers who deliberately want full request persistence (the old TECHNICAL_PLAN already
  promises this flag).
- Excluded headers are dropped at envelope-construction time, not at replay time, so they
  never reach the store.
- Document that replayed requests acquire auth from the pipeline, and confirm the
  orchestrator's transport can pick up whatever the app's auth handler provides — see
  Notes.

## Acceptance Criteria

- [ ] README states that headers set before Hyperwyc sees the request — credentials included —
      are persisted with the envelope, and why that is necessary rather than incidental.
- [ ] That guidance sits alongside, and links to, the encryption-key documentation, so the
      exposure and its mitigation are read together.
- [ ] The handler-ordering recommendation gains its second reason: credentials added after
      Hyperwyc are never stored at all.
- [ ] [docs/storage.md](../docs/storage.md) states the same, replacing the claim that sensitive headers are excluded.
- [ ] Unit test: a caller-set `Authorization` survives queueing and replay unchanged — the
      fidelity guarantee, pinned so a future "security fix" cannot quietly break replay.
- [ ] Decision recorded on whether an opt-in deny-list ships at all.

## Why a deny-list is the wrong answer

Two things changed the conclusion.

**[ADR 0002](../docs/decisions/0002-replays-traverse-the-pipeline.md) removed most of the
exposure.** With the recommended ordering, a credential added by a *handler* is never captured —
the handler runs after Hyperwyc has serialised the envelope. `Cookie` is never captured either,
since cookies are attached by the primary handler's `CookieContainer`, below Hyperwyc. What is
left is the credential a caller sets directly on the request.

**And that one must be persisted.** [ADR 0001](../docs/decisions/0001-idempotency-is-not-hyperwycs-remit.md)
commits Hyperwyc to fidelity: *"Being naive must not mean being lossy. If Hyperwyc declines to
add anything, it must faithfully carry everything the application did set."* A deny-list that
silently drops a caller-set header is exactly the lossiness that obligation rules out.

It is not merely inconsistent, it would break working applications. A credential is not always a
short-lived token:

| Credential | Still valid at replay? |
|---|---|
| Bearer token, short-lived | No — but that one is added by a handler and never captured |
| API key | Yes |
| Basic auth | Yes |
| HMAC over stable request content | Yes |

For three of those four, stripping the header turns a replay that would have succeeded into one
that cannot. Hyperwyc would be breaking delivery in the name of protecting the consumer from a
decision the consumer made deliberately.

There is a deeper point. Deciding which headers are "sensitive" is a judgement about the
application's threat model, made by a transport library that knows nothing about it. That is the
same imposition [ADR 0001](../docs/decisions/0001-idempotency-is-not-hyperwycs-remit.md) declined
over idempotency, and the answer is the same: surface the fact, do not decide on their behalf.

## What this becomes instead

**Documentation, stated plainly and where people will meet it.**

Anything attached to a request before it reaches Hyperwyc — including credentials — is persisted
to disk with the queued envelope, because that is the only way a replay can reproduce the request
the application made. Consumers should know that, and should know the two levers they have:

- Add credentials in a handler registered *after* `AddHyperwycHandler()`, in which case they are
  never captured at all and are minted fresh at replay time. This is already the recommended
  ordering, now with a second reason behind it.
- Supply their own encryption key via `CabinetStoreOptions.EncryptionKey` if what does get stored
  warrants better than the path-derived default ([issue 32](32-default-encryption-key.md)).

The deterministic default key is the sharper end of this, and the documentation should connect
the two rather than treating them as unrelated topics: *these are the circumstances in which
something worth protecting ends up on disk, and this is how you protect it properly.*

### An optional deny-list, inverted

If the capability is wanted at all, it should invert from what this item originally proposed:
persist everything by default — fidelity — with an **opt-in** list of header names a consumer
chooses to have stripped. That way the consumer makes the judgement about their own threat model,
and accepts the replay consequences knowingly, rather than Hyperwyc deciding for them.

Worth doing only if someone asks for it. The documentation is the part that is actually needed.

## Notes

- Discovered while reconciling the docs against the code; not previously tracked.
- **Resolved by issue #37.** This item's hardest question — how replayed requests acquire
  credentials once the persisted `Authorization` header is dropped — is answered: replays now
  traverse the originating client's pipeline, so the application's auth handler stamps a fresh
  token at replay time. Excluding sensitive headers is therefore straightforwardly correct
  rather than a change that breaks replay. Note that with the recommended ordering the header
  was never captured in the first place, so the exclusion is mostly a defence against
  consumers who register auth *before* Hyperwyc.
