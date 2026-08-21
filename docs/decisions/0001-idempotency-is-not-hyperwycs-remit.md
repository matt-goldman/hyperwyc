# 1. Idempotency is not Hyperwyc's remit

**Status:** Accepted — implemented in [issue 39](../../Backlog/Done/39-reconsider-idempotency.md),
superseding [issue 08](../../Backlog/Done/08-idempotency-key-injection.md).

## Context

Hyperwyc injected an `Idempotency-Key` header on every mutating request, reusing the same value
across replays so a server could suppress duplicates. The reasoning was sound on its face: a
queued write may be delivered more than once, and a stable key is the conventional defence.

The problem is what it quietly required. The header only does anything if the backend implements
it, so a library whose entire positioning is *slots into your existing architecture* was in fact
asking every consumer to change their API. That is the thing Hyperwyc exists not to do — the
distinction it draws against Realm, CommunityToolkit.Datasync and similar, which require you to
architect around them.

The cost showed up concretely in our own sample. `Shared.Sale` already carried a
client-generated `Guid Id`, yet the sample API grew an idempotency-key dictionary and a lookup on
every `POST /sales`, existing *solely* because Hyperwyc sent the header. We wrote backend code to
satisfy our own library while the domain already had everything it needed.

## Decision

**Hyperwyc adds nothing to outbound requests, and takes no position on duplicate suppression.**

The header injection is removed. Duplicate suppression is a concern between an application and
its API, and Hyperwyc is naive about it.

## Rationale

Three strands, of which the first is the strongest.

**It is not a problem Hyperwyc creates.** Duplicate delivery is a property of *retrying*. A Polly
retry handler, a user double-tapping a button, or a proxy replaying a request all produce it. The
API copes or it does not, and application code interprets whatever comes back. Hyperwyc's retry
carries exactly this risk and no more — the same problem, with the same owners, whether or not
Hyperwyc is present. Solving it here meant solving a general HTTP concern inside a library whose
remit is much narrower.

**It duplicated a mechanism at the wrong layer.** Hyperwyc's handler is registered first, and the
first handler added is the last to see the response, so it observes each response *after* every
handler the application added. Anything the application already resolves — a refresh-on-401, a circuit
breaker, its own retry — is resolved before Hyperwyc sees anything. Building a second mechanism
above that is redundant at best and interfering at worst.

**Removing it took nothing away.** An application that wants the header sets it at the call site,
which is where "this is one logical operation" is actually known. Hyperwyc persists request
headers and replays them byte-identically, so such a key is already stable across every attempt.
The capability was never blocked; only the imposition was.

That the header is an
[IETF draft](https://datatracker.ietf.org/doc/draft-ietf-httpapi-idempotency-key-header/) rather
than a Hyperwyc invention makes it respectable, and makes it reasonable for an application to
adopt. It does not make emitting it Hyperwyc's job. Being a standard is an argument for a thing
being *available*, not for it being *automatic*.

## Consequences

**Accepted downside: no duplicate protection out of the box.** A write retried after its response
was lost can be recorded twice, and Hyperwyc will not prevent it. This is stated plainly in the
README rather than glossed. It is worth being clear that this is not a regression in real safety
— the previous behaviour only protected consumers whose backend implemented the header, and
merely *looked* safe to everyone else.

**An obligation this creates: fidelity.** Being naive must not mean being lossy. If Hyperwyc
declines to add anything, it must faithfully carry everything the application did set — headers
survive queueing and every replay attempt unchanged. That is now the load-bearing guarantee
behind application-level idempotency, and it is pinned by a test that fails if a replayed request
carries a different key from the original.

**`Envelope.Id` became internal.** It previously doubled as the header value, conflating
Hyperwyc's identity for a queued item with an application-facing value. It is now a plain GUID
that is never sent.

## The test this establishes

The point of recording this is not the header. It is to have a way of answering the next question
of the same shape, because the pull toward adding capability is constant and each addition looks
reasonable in isolation.

Before adding a feature, responsibility, or configuration knob:

1. **Would this problem exist without Hyperwyc?** If yes, it is a general concern with existing
   owners. Not ours.
2. **Does it require anything of the consumer's API?** If yes, it is an imposition, whatever its
   merits. Not ours.
3. **Can the application already do this** — at the call site, or with its own `DelegatingHandler`?
   If yes, our job is to not interfere, not to do it for them.
4. **Does it depend on something only Hyperwyc knows?** If no, it almost certainly belongs
   elsewhere.

Question 4 is the positive form, and it is short. What Hyperwyc uniquely knows is:

- whether the device is currently reachable;
- that a given request was queued rather than sent;
- that a given request is a *replay* of a specific queued one;
- what is sitting in the durable outbox.

Features grounded in those facts are candidates. Features grounded in anything else are somebody
else's — usually the application's, sometimes the API's.

A useful sanity check on any "no" answer: if the capability is genuinely wanted, can a consumer
still reach it in a few lines? If so, declining costs them very little, and it costs everyone else
nothing at all.

## Prior instances of the same principle

Decided before this folder existed, recorded in their backlog items:

- [Issue 37](../../Backlog/Done/37-replay-through-pipeline.md) — replays traverse the
  application's pipeline rather than bypassing it, so auth is the application's handler's job
  rather than something Hyperwyc reimplements.
- [Issue 38](../../Backlog/Done/38-retry-classification.md) — Hyperwyc retries connectivity
  failures on connectivity change, and defers every other failure class to whatever the
  application already does.

This ADR is the third instance, and the clearest, because here the correct contribution turned
out to be nothing at all.
