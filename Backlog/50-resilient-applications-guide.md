# Issue 50 — "Designing Resilient Applications with Hyperwyc"

## Summary

A guidance document for applications that need more than Hyperwyc offers: how to read the
headers and events it surfaces, and how to build guaranteed delivery on top of a component that
deliberately does not provide it.

Placeholder. Captured so the idea is not lost, not specified.

## Status

⬜ Open, unscheduled. Filed 2026-08-23.

## Why it needs to exist

Hyperwyc is transport-level and says so repeatedly — no duplicate suppression
([ADR 0001](../docs/decisions/0001-idempotency-is-not-hyperwycs-remit.md)), no conflict
resolution, no delivery guarantee, and an unreadable store is reported rather than repaired
([issue 49](49-unreadable-store-recovery.md)).

Each of those is right on its own and correctly documented where it arises. The gap is that a
reader assembling them into an actual application has to do that assembly themselves, from a
dozen scattered caveats. The recurring question underneath is the same one every time: *what do I
build if I need this to be reliable?*

The short answer is usually **an application-owned store alongside Hyperwyc's**. The application
records its own intent durably, treats Hyperwyc as the delivery mechanism, and reconciles against
what comes back on `SyncEvents`. That is a coherent architecture, it is what a serious consumer
will end up at anyway, and nothing currently describes it.

Getting that written down is also what allows Hyperwyc to keep saying no. "Not our remit" is a
much stronger position when it is followed by a page explaining what to do instead.

## Rough contents

Not a specification — a list of what has accumulated and would belong here.

- **What Hyperwyc surfaces, and how to use it.** `X-Hyperwyc-Status` and
  `X-Hyperwyc-Correlation-Id` on synthetic responses, and the `SyncEvent` stream with its
  `SyncOutcome` ([issue 40](Done/40-surface-deferred-outcomes.md)). Correlating a `202` back to a
  local record is the foundation everything else builds on.
- **The application-owned store pattern.** Recording intent locally, driving UI from that rather
  than from Hyperwyc, and using events to reconcile. This is the core of the document.
- **Duplicate delivery.** Why it is not Hyperwyc's problem, and the options: a caller-set
  `Idempotency-Key`, a domain-level natural key, or server-side dedup.
- **"Submitted" rather than "successful".** The UI framing already noted in issue 40, expanded —
  a deferred write has no result yet, and modelling one is how applications end up lying to
  users.
- **Restore and reinstall.** A restored backup can re-assert writes that already happened
  ([issue 48](48-exclude-store-from-os-backup.md)). What an application-owned store lets you do
  about it that Hyperwyc cannot.
- **When the store is unusable.** Hyperwyc degrades to pass-through and reports it; what a
  resilient application does with that.
- **When not to use Hyperwyc for something.** Large binary uploads, long-running operations,
  anything needing ordering guarantees across resources.

## Notes

- Author's idea, captured 2026-08-23 while closing out
  [issue 49](49-unreadable-store-recovery.md): "In the back of my mind I have a 'Designing
  resilient applications with Hyperwyc' doc that comes somewhere later, with guidance for how to
  handle the injected headers and respond to events when you need guaranteed delivery. Which
  typically means an application owned store in addition to what Hyperwyc caches for you."
- **Deliberately unscheduled.** It should be written once the surface has stopped moving —
  per-route policies ([22](22-v1-per-route-policies.md)) and binary bodies
  ([25](25-binary-request-response-bodies.md)) both change what advice is correct. Writing it
  early means rewriting it.
- Belongs in `docs/`, not the README. The README answers "how do I use it"; this answers "how do
  I build on it", which is a different reader on a different day.
- When it is written, the scattered caveats it collects should link *to* it rather than being
  duplicated into it.
