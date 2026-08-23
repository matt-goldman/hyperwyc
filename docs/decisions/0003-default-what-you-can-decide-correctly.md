# 3. Default what you can decide correctly; require what you cannot

**Status:** Accepted — implemented in
[issue 47](../../Backlog/Done/47-connectivity-is-required.md), revising
[issue 31](../../Backlog/Done/31-package-structure.md) and
[issue 13](../../Backlog/Done/13-connectivity-reference-implementation.md).

## Context

Hyperwyc's registration story was, deliberately, that it works out of the box.
`AddHyperwyc()` with no arguments gave you a durable, encrypted Cabinet store and a set of
sensible defaults, and the README said so in as many words: *that's the whole setup.*

`HyperwycOptions.Connectivity` defaulted to `AlwaysOnlineConnectivityService` in service of
that promise. It was the one default that could not be justified the same way as the others.

Under it, `IsConnected` is always `true`. Every request takes the network path. Nothing is
ever queued, nothing is ever replayed, and the outbox stays empty forever. A consumer who
installed the package, called `AddHyperwyc()` and shipped would have a caching library that
silently dropped every offline write — no exception, no log line, no failing test. It looks
like it works, and the case where it does not is the case the library exists for.

The same reasoning had already been accepted for the store. `HyperwycOptions.Store` was
removed in issue 31 and replaced with a type parameter, so that omitting a store is a compile
error rather than a silent fall back to `InMemorySyncStore`. Connectivity was the identical
failure — an invisible default producing an application that runs and does not work — sitting
one property away, unnoticed.

## Decision

**A default is legitimate when Hyperwyc has the knowledge to choose correctly. When it does
not, and being wrong is invisible, the option is required and registration fails loudly.**

Concretely: `Connectivity` has no default. A consumer registers an `IConnectivityService` in
the container, or sets the option; doing neither throws an `InvalidOperationException` naming
the three implementations available, because the fix is a decision rather than a forgotten
line.

## Rationale

The distinction is not "important" versus "unimportant". Storage is at least as important as
connectivity; a bad store loses data outright. The distinction is **who holds the knowledge**.

**Storage: any durable store will do.** The choice is real but not load-bearing. Cabinet works
everywhere Hyperwyc runs, and a consumer who would have preferred something else gets a
working library and can change it later without having been harmed. Hyperwyc has everything it
needs to pick.

**Connectivity: only the application knows.** How a device reports reachability depends on the
platform, and Hyperwyc is deliberately platform-independent — that independence is why the
MAUI implementation stays out of the package (issue 13). Hyperwyc is structurally incapable of
making the right choice here. Guessing is not a default; it is a fabrication.

The second half matters as much as the first. A wrong default is tolerable when being wrong is
*loud* — you find out, and you fix it. `AlwaysOnline` is wrong quietly, and quietly wrong is
the worst behaviour a library of this kind can have, because the consumer's own testing
reproduces the appearance of success.

## Consequences

**Zero configuration is now a narrower claim, and an accurate one.** Storage needs no decision;
connectivity needs exactly one. The README says that instead of *that's the whole setup*.
That is a real cost against issue 31's headline, and it is the right trade — "works with no
configuration" was only ever true in the sense that it *ran*.

**A required option needs somewhere to go.** Failing at startup is only reasonable if the fix
is short, so issue 47 also ships `NetworkAvailabilityConnectivityService` — BCL-only, no
platform dependency — so a non-MAUI consumer has an honest answer that is not "pretend you are
always online". `AlwaysOnlineConnectivityService` stays, as an explicit opt-in for hosts that
genuinely are always connected, or for consumers who want only the response cache.

**A required option must not become a required *ordering*.** Requiring a decision is not the
same as dictating how it is expressed, and the container is where most consumers will express
it. The check is therefore deferred to resolution rather than made at registration, so a
registration works before or after `AddHyperwyc` — including one emitted by a source generator,
which has no say in where its output lands. That trades fail-fast for order-independence: the
consumer who supplied nothing finds out on first use instead of at startup.

Worth being explicit about the direction of that trade, because the instinct runs the other
way. Fail-fast is a benefit to someone who has made a mistake. Order-independence is a benefit
to someone who has not. The second group is larger and has a stronger claim, and the error
message is equally unmissable whenever it arrives.

## The test this establishes

[ADR 0001](0001-idempotency-is-not-hyperwycs-remit.md) asks whether a capability belongs in
Hyperwyc at all. This asks a narrower question about the ones that do: **should this have a
default?**

1. **Can Hyperwyc choose correctly from what it knows?** If the right answer depends on the
   platform, the API, or the domain, it cannot — that knowledge lives with the application.
2. **If the default is wrong, does the consumer find out?** A wrong-but-loud default is fine;
   they hit it, they change it. A wrong-but-silent default ships to production.
3. **Is the failure the one the library exists to prevent?** Defaults that can quietly produce
   *that* failure are never acceptable, whatever the ergonomic cost.
4. **If it must be required, is the fix short and obvious?** A required option is only
   reasonable if the error message closes the gap. Ship an implementation to point at.

Question 2 is the load-bearing one. Convenience is worth a great deal, and Hyperwyc should keep
defaulting aggressively — but only where a mistake announces itself.

## Related

- [Issue 31](../../Backlog/Done/31-package-structure.md) — the store became a type parameter for
  the same reason, reached first and generalised here.
- [Issue 32](../../Backlog/32-default-encryption-key.md) — the path-derived encryption key
  *stays* as a default, and is not a counter-example: it is weak, but its weakness is documented
  and visible in the threat model rather than hidden in the runtime behaviour. Question 2 passes.
- [Issue 13](../../Backlog/Done/13-connectivity-reference-implementation.md) — why the MAUI
  implementation is copied rather than shipped, which is what made a required option necessary
  rather than merely defensible.
