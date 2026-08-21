# 2. Replays traverse the application's pipeline

**Status:** Accepted — implemented in
[issue 37](../../Backlog/Done/37-replay-through-pipeline.md).

## Context

A queued write has to be sent eventually, and the original design sent it through a bare
transport: the orchestrator held its own `HttpMessageHandler` and replayed the persisted
envelope directly to the origin. That seemed obviously right. The alternative — sending a replay
back through the very pipeline that queued it — looked like it would simply queue it again.

It was wrong, and wrong in a way that broke the library's headline feature against most real
APIs.

`HyperwycHandler` is registered **first**, so it sees a request before any handler registered
after it, and serialises it into an envelope *before* those handlers have run. With the ordering the documentation
recommended — Hyperwyc first, auth after — that means the envelope never contained an
`Authorization` header, because the auth handler had not run yet. Replaying that envelope
through a bare transport sent an unauthenticated request, which was rejected, retried, and
dead-lettered.

Reversing the order does not rescue it. Register auth first and the envelope captures the token
that was valid when the request was *queued* — hours or days stale by the time connectivity
returns. That is precisely the problem the recommended ordering existed to avoid.

**So offline writes did not work against an authenticated API in either configuration**, while
the documentation described, in detail, a behaviour the implementation never had.

## Decision

**A replayed request is sent through the same `HttpClient` pipeline it was originally made on.
`HyperwycHandler` recognises the replay and steps aside; every other handler runs normally.**

The orchestrator resolves the originating named client through `IHttpClientFactory`, using a
client name captured at registration and stamped onto the envelope. The handler detects a
marker on the request and delegates straight to `base.SendAsync`.

Register `HyperwycHandler` **first** — closest to your calling code, furthest from the network.
Everything registered after it applies to ordinary requests and replays alike.

```
your code  →  HyperwycHandler  →  AuthHandler  →  network
              (added first)       (added second)
```

Both "outermost" and "innermost" get used for this and they pull in opposite directions
depending on whether you picture nesting or proximity to the wire, so this document avoids both.
What matters is the order of `Add…` calls: **first added, first to see the request, last to see
the response.**

## Consequences

**A write queued on Monday and replayed on Tuesday is authenticated with Tuesday's token.** The
credential is minted at send time by the application's own handler, which is the only place that
can know what a valid credential currently is.

**It generalises past auth.** Logging, correlation identifiers, telemetry, custom retry, circuit
breakers — anything registered after Hyperwyc now applies to replayed traffic too, without
Hyperwyc knowing those concerns exist.

**A marker is required, not inference.** "This request is a replay" cannot be deduced from
connectivity: a replay reaching the normal online path would publish a second `OnSynced` and
re-run cache invalidation, work the orchestrator has already taken responsibility for. The
marker is set per attempt, since a fresh `HttpRequestMessage` is built for each one.

**Envelopes record their originating client.** An application may have several named clients
using Hyperwyc, with different base addresses and different auth. A replay must return through
the one it came from, so the envelope carries a client name — a change to the persisted shape.

**Registration changed** from `AddHttpMessageHandler<HyperwycHandler>()` to
`AddHyperwycHandler()`, which captures the client name from `IHttpClientBuilder.Name`. The plain
form still works but yields no name, in which case replays fall back to
`HyperwycOptions.ReplayTransport` — a bare transport, with none of the application's handlers.

**`Microsoft.Extensions.Http` became a core dependency.** Reaching the pipeline requires
`IHttpClientFactory`. Any consumer calling `AddHttpClient` already has it.

## What this decides for future questions

**Replay fidelity is about the pipeline, not the bytes.** The instinct that a replay should
reproduce the original request *exactly* is wrong wherever the request contains something
time-sensitive. A credential, a signature, a timestamp, a nonce — anything minted at send time
should be minted again at replay time, by whatever minted it originally.

The envelope is therefore best understood as **the request as the application expressed it**,
not as the bytes that went on the wire. Everything the pipeline would add on the way out is
added again on the way out again.

That reasoning settled two later questions on its own:

- [Issue 39](../../Backlog/Done/39-reconsider-idempotency.md) — because a header set at the call
  site *is* part of how the application expressed the request, it is persisted and replayed
  verbatim, and Hyperwyc needs to add nothing of its own.
- [Issue 38](../../Backlog/Done/38-retry-classification.md) — because Hyperwyc's retry wraps the
  whole pipeline, it observes only failures the application's own handlers could not resolve,
  and so has no business duplicating them.

**Where it does not reach.** Two behaviours look like exceptions and are not:

- On the **offline path**, a synthetic response short-circuits the pipeline, so nothing
  downstream runs. There is no outbound request to authenticate or stamp.
- A **queued envelope reflects the request as Hyperwyc saw it**, before downstream handlers ran.
  That is not a gap to be filled by capturing more; it is the reason replays must traverse the
  pipeline rather than being replayed verbatim.
