# Issue 63 — Should the Synthetic Status Codes Be Configurable?

## Summary

Two author's questions in `docs/responses.md`, which are the same question twice: should the
`202 Accepted` for a queued write, and the `200 OK` for a read with nothing to serve, be
configurable — globally, or per route policy?

## Status

💭 Under consideration. Filed 2026-09-08. **Recommendation: no**, and say so on the page.

## The `202`

The `202` is the only in-band signal separating a queued write from a delivered one. Making it
configurable lets a consumer configure that distinction away, and the failure mode is both silent
and severe: a caller treats a queued write as done. Defaults test question 3.

The scope test points the same way. A consumer who needs a different code has one line at the
call site, or one handler registered above Hyperwyc's, so question 3 — can the application
already do this itself — says our job is to not interfere.

And the constraint already documented on the page, *"only if no ordinary success from your API is
a `202`"*, is the argument a consumer will bring, and a knob does not relieve it. An API that
genuinely returns `202`s needs the header rather than a different code, because whatever code was
configured could collide the same way. The knob moves the problem.

## The `200`

Stronger still, because a consumer choosing `404` would be re-arming the exception the design
exists to remove. Three reasons the `200` is right, which are also the answer to the page's
second TODO and should be written there:

- **A `404` asserts the resource does not exist.** Hyperwyc does not know that and it is usually
  false — the resource exists, we cannot reach it. Asserting it on the server's behalf is the
  same overreach as stamping a `Content-Type` on a route we know nothing about, which
  `HyperwycResponseFactory` explicitly declines to do.
- **It is permanent, and callers treat it that way.** `EnsureSuccessStatusCode` throws on it,
  `GetFromJsonAsync` throws on it, applications cache "this one does not exist". A read that
  failed because of connectivity is the most transient condition there is; encoding it as the
  most permanent status is backwards.
- **It puts the exception back.** The `null` body exists so that an offline read returns rather
  than throws. A 4xx hands the throw straight back through the JSON extension methods, undoing
  the thing the body was chosen for.

`503` fails on the first of those too — it asserts something about the server, and the server is
fine.

So "this request produced no data" is the only honest statement available, and `200` + `null` +
`X-Hyperwyc-Status: Offline` is the only combination that makes it without asserting something
false.

## Precedent

This is [item 26](Done/26-v2-typed-response-shaping.md)'s shape. That item was closed unbuilt
after Layer 0 — return `null` rather than an empty body — shipped separately and removed the
sharp edge; what remained was a configuration knob for something the consumer writes at the call
site anyway. Same conclusion here, reached the same way.

## Acceptance Criteria

- [ ] `docs/responses.md` carries the reasoning for both codes as deliberate absences, in the
      shape of the other "we do not do this, here is why" notes.
- [ ] `delivery.md`'s "Why no data instead of no connection" box links to it rather than
      duplicating it — that box makes the application-design argument, this makes the
      status-code argument, and they are different claims that currently blur.
- [ ] Both TODOs in `responses.md` are gone.

## Notes

- The two questions were filed separately in the document and are one decision: whether Hyperwyc's
  declines become knobs. Answering them together is what makes the reasoning transferable to the
  next one.
