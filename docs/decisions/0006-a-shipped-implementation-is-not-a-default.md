# 6. A shipped implementation is not a default

**Status:** Accepted — decided during
[issue 47](../../Backlog/Done/47-connectivity-is-required.md), alongside
[ADR 0003](0003-default-what-you-can-decide-correctly.md).

## Context

[ADR 0003](0003-default-what-you-can-decide-correctly.md) settled that connectivity is required:
`HyperwycOptions.Connectivity` has no default, and supplying nothing throws. Its argument was
aimed at `AlwaysOnlineConnectivityService`, the default it replaced — a service that reports
connected forever, under which nothing is ever queued and nothing ever replayed.

That argument does not dispose of the option actually on the table.

Three candidates were weighed. The third was **a working BCL default**:
`NetworkInterface.GetIsNetworkAvailable()` for state and `NetworkChange.NetworkAvailabilityChanged`
for the stream — dependency-free, cross-platform, and genuinely functional. It was the
recommended option at the time, and it would have preserved the zero-configuration headline from
[issue 31](../../Backlog/Done/31-package-structure.md) while fixing the silent failure.

ADR 0003's rationale — *the right answer depends on the platform, which only the application
knows* — is the argument against `AlwaysOnline`. It is not an argument against this one. A BCL
service needs no platform knowledge; it runs everywhere Hyperwyc runs. Read literally, 0003
permits it.

So the question 0003 leaves open, and the one this records: **Hyperwyc now ships an
implementation that would work. Why is it not registered when nobody supplies one?**

## Decision

**Hyperwyc ships connectivity implementations and registers none of them.** Both
`NetworkAvailabilityConnectivityService` and `AlwaysOnlineConnectivityService` are one line to
adopt and neither is ever adopted on the consumer's behalf. Supplying nothing throws, naming all
three routes.

Generally: **shipping an implementation is how a required decision is made cheap. Registering it
automatically is how the decision disappears.** Those are different acts, and the second undoes
the first.

## Rationale

**The service is wrong where Hyperwyc matters most.** `GetIsNetworkAvailable()` reports whether a
network is available, not whether your API is reachable. On a desktop or a server that is very
nearly the same question. On mobile it is not: a captive portal, a router with no upstream, or a
signal too weak to carry a request all report connected. Mobile is the case this library exists
for. A default that is accurate on the host where offline barely happens and unreliable on the
host where it happens constantly is precisely the wrong default — it would be least trustworthy
exactly where it was most load-bearing.

**The cost of being wrong is small, and that is not the point.** A false "connected" costs a
wasted attempt and some latency, not correctness: the request goes out, the transport reports
the truth, and the flush is abandoned until the next signal. The objection is not danger, it is
**attribution**. A consumer who chose this service has read the sentence about captive portals
and can account for what they see. A consumer who inherited it cannot, because they do not know
it is there.

**A consumer should knowingly pull the trigger.** Every implementation Hyperwyc ships is
imperfect in a way the consumer has to evaluate against their own API and platform —
`AlwaysOnline` obviously so, `NetworkAvailability` subtly so. An imperfection you accepted is a
trade. The same imperfection arriving unannounced is a defect, and it is one you will diagnose
as a bug in Hyperwyc, because nothing told you a choice was made.

**The error message is the teaching moment, and a default removes it.** The exception naming the
three routes is how a consumer learns that connectivity is a decision at all. Behind a working
default it never fires, and the lesson lands later — in production, on a device, as writes that
did not queue.

**It is the same shape as `AlwaysOnline`, which nobody would auto-register.** The two services
sit at different points on one line, not in different categories. Once you accept that a shipped
implementation must be asked for, `NetworkAvailability` being *better* does not change what kind
of thing it is.

## What this is not

**Not a rule against defaults.** Hyperwyc defaults aggressively and should keep doing so.
Cabinet *is* registered without being asked for, because any durable store will do and being
wrong is recoverable and visible — 0003's test passes cleanly. This ADR narrows nothing about
that; it addresses the case where the test has already come out the other way and a plausible
implementation then presents itself as a way around the answer.

**Not an argument that the implementation is bad.** It is good, it is tested, and it is the right
choice for a desktop or server host. It ships for that reason. This is about registration, not
quality.

## Consequences

**`AddHyperwyc()` alone throws.** The shortest working registration is one line more than it
would have been. Accepted in 0003 and unchanged here.

**Two shipped implementations nobody gets by accident.** Neither is dead code — each is a
documented one-liner — and the docs can be blunt about the limitations of both, because a
consumer reads them *before* choosing rather than after being surprised.

**The route back to zero configuration is discovery, not fabrication.** A source generator that
finds the single `IConnectivityService` an assembly declares and registers it would restore
no-configuration setup without any of this being untrue: it registers the consumer's own choice,
discovered, rather than one Hyperwyc invented. That is why the requirement check is deferred to
resolution rather than made at registration (0003), and it is the only form of "default
connectivity" that is compatible with this decision.

## The test this establishes

An addition to the [standing defaults test](README.md#the-standing-defaults-test), for the case
where an implementation exists and defaulting to it looks free:

5. **Would this default be equally right everywhere Hyperwyc runs?** An implementation that is
   accurate on one host and unreliable on another can ship, but must be named. Weight that by
   where the library is actually needed — a default that degrades precisely in the conditions the
   library exists for is worse than no default, because it is trusted in the one place it should
   not be.

## Related

- [ADR 0003](0003-default-what-you-can-decide-correctly.md) — establishes that connectivity is
  required. This closes the gap in that argument: it explains why a *working* default was
  refused, not merely a broken one.
- [ADR 0004](0004-default-to-removal.md) — the removal here is of an automatic registration, not
  of code. The implementations stayed; only the assumption that they should be applied for you
  came out.
- [Issue 13](../../Backlog/Done/13-connectivity-reference-implementation.md) — why the MAUI
  implementation is copied into the consumer rather than shipped, which is what made a required
  option necessary in the first place.
