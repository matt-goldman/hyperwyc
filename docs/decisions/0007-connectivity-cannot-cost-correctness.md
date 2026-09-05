# 7. Connectivity is an optimisation, not a correctness input

**Status:** Accepted — supersedes
[ADR 0006](0006-a-shipped-implementation-is-not-a-default.md) and withdraws the premise of
[ADR 0003](0003-default-what-you-can-decide-correctly.md). Implemented 2026-09-06.

## This corrects a defect; it is not a change of mind

ADR 0003 required a connectivity implementation, and ADR 0006 refused to register one for you.
Both rested on a single claim: **that a wrong connectivity answer costs correctness.** 0003 put
it as an application that "runs and does not work"; 0006 as an imperfection the consumer must
knowingly accept.

That claim was an accurate description of the code and an inaccurate description of the design.
It was true because of a bug, and we reasoned about the bug as though it were a constraint.

**The bug.** When `IConnectivityService` reported online and the transport could not reach the
API, Hyperwyc did not degrade. An online write threw and queued nothing — the library's central
promise, unmet in exactly the conditions it exists for. An online read threw too, unless the
route was `NetworkFirst` *and* a cached copy was still inside its TTL. Offline, both cases were
handled properly. So the same device in the same state got two different behaviours depending on
what a service *claimed*, and the claim was load-bearing only because the fallback was missing.

**Where it came from**, because the shape is worth recognising: commit `407b2c1`, "Fix caching
strategy and TTL", added the read fallback. That commit is where we first noticed the transport
can contradict the connectivity service. It was framed as a caching fix, so the insight was
applied to reads and never carried to writes. **An insight reached inside a narrow frame does not
escape it on its own.** ADR 0003 was then written over a codebase that already contained the
answer for reads and the defect for writes, and generalised from the defect.

Both are now fixed. A read the transport cannot answer is served from the store or answered
`Offline`; a write the transport never delivered is queued and answered `202`. A caller cannot
tell whether the connectivity service was right.

## Context

With that fixed, what does `IConnectivityService` still do?

It decides **which path to try first**. Reporting offline means Hyperwyc goes straight to the
store instead of attempting a request that will fail. Reporting a change is what causes the
outbox to drain. Neither of those is a correctness property; they are latency, battery, and
timeliness.

The transport is what actually knows, and it now always gets the last word.

## Decision

**A connectivity implementation is optional. When none is supplied, Hyperwyc uses
`NetworkAvailabilityConnectivityService` and says so once, at `Information`, naming the platform
alternatives.**

Registering one in the container remains the expected route and still wins, in either order
relative to `AddHyperwyc`. `AddHyperwyc()` alone now resolves a working system.

## Rationale

**Question 2 of the standing defaults test stops being decisive.** *"If the default is wrong,
does the consumer find out?"* mattered because being wrong meant losing writes silently. Being
wrong now means a doomed request, then the correct behaviour. A default whose worst case is
latency does not need to be discovered urgently — and it is announced anyway.

**The fallback is the one that keeps the promise.** `NetworkAvailabilityConnectivityService`,
never `AlwaysOnlineConnectivityService`. The distinction is not accuracy, it is
`ConnectivityChanged`: `AlwaysOnline` never emits, so under it nothing would ever trigger a
flush and queued writes would wait for a restart. That would be a new silent failure wearing the
old one's clothes. The BCL service emits on interface changes, so the outbox drains without the
consumer doing anything.

**0006's argument does not survive its own premise.** It said the same signal means opposite
things depending on topology — a machine with only a VPN interface up is offline if your API is
on the internet and online if your API is in the tunnel — and that Hyperwyc cannot tell which.
That observation is still true and still good. What has changed is the consequence: being wrong
about it used to lose a write and now costs an attempt. An ambiguity worth stopping a consumer
over is not the same as an ambiguity worth mentioning to them.

**What a required option was actually buying.** It forced a decision at the moment of least
context — before the consumer had run anything — in exchange for preventing a failure that no
longer exists. The cost was real and paid by everyone; the benefit is now zero.

## Consequences

**Zero configuration is true again, and this time accurately.** `AddHyperwyc()` gives a durable
encrypted store and a working connectivity source. [Issue 31](../../Backlog/Done/31-package-structure.md)'s
headline is restored, having been narrowed by 0003 for a reason that has since been removed.

**Mobile consumers still want their own.** The fallback reports link state, not reachability, so
on mobile it is wrong often — captive portal, weak signal, a VPN interface. Each wrong answer
costs a doomed request before the correct behaviour, which on a phone is worth avoiding. The docs
and the startup notice both say so. That is advice now, not a gate.

**`AlwaysOnlineConnectivityService` needs its own warning, and it is not the old one.** Writes
survive under it. What does not is replay: `ConnectivityChanged` never emits, so the only
triggers left are `FlushOnStartup` and an explicit `FlushAsync()`. Draining the outbox becomes
the application's job — a scheduled task, a user-facing control, a duty cycle on a headless
device. That is a legitimate design for an IoT fleet and a trap for someone who picked it to make
an error message go away.

**ADR 0003's decision stands where its reasoning still holds.** The general form — *default what
you can decide correctly, require what you cannot* — is untouched and remains the test. What is
withdrawn is its application to connectivity, because the premise that made connectivity fail
question 2 was a defect. The store is still defaulted, and still for the reason 0003 gave.

## The test this establishes

A question to ask *before* the standing defaults test, because it decides whether that test
applies at all:

0. **Is this input actually load-bearing, or does something downstream already know better?**
   An input the system can verify for itself is a hint, and a hint may be defaulted. If the
   answer is "we would find out anyway, one layer down", the decision is about efficiency, and
   requiring the consumer to make it is a cost with no return.

And a warning about how this one was missed for so long: **a defect that has been reasoned about
becomes a premise.** Once ADR 0003 wrote down that connectivity was load-bearing, every later
decision inherited it, including 0006, which built a second argument on the same foundation
without re-examining it. When an ADR rests on a claim about how the system behaves, that claim
is worth re-checking against the code, not just against the previous ADR.

## Related

- [ADR 0003](0003-default-what-you-can-decide-correctly.md) — the reasoning is intact and still
  the standing test; its connectivity conclusion is withdrawn here.
- [ADR 0006](0006-a-shipped-implementation-is-not-a-default.md) — superseded. Its topology
  argument is still correct and now appears in the docs as guidance.
- [ADR 0004](0004-default-to-removal.md) — the fix removed a branch, a status classification and
  two enum members while closing the gap. Consistent with the pattern that adding the right thing
  usually takes something out.
