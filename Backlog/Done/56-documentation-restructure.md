# Issue 56 — Documentation Mixes Instruction and Explanation, With No Path Through It

## Summary

The documentation is good and there is a lot of it. What it does not have is an order: nearly
every page interleaves "here is what you do" with "here is why it works this way", so a reader
looking for either has to read past the other. Add a tutorial, add per-host quick starts, and
separate the reference pages from the explanation they currently carry.

Filed from the review of 2026-09-08. Collects the author's TODOs at `docs/delivery.md` and
`docs/events.md`, both of which reach the same conclusion independently.

## Status

✅ Done, 2026-09-08. Four new documents, one rewritten, six stripped back to reference, and the
navigation rebuilt around the four readers. `connectivity.md` went from 337 lines to 146 without
losing anything — the material moved to the page whose reader wanted it.

**The tutorial is verified.** Every command, snippet and output in it was run end to end against
the real library before being written down.

## The problem, stated precisely

Four readers are being served by one set of pages:

| Reader | Question | Where it is answered today |
|---|---|---|
| Evaluator | Should I use this? | `choosing.md` — the one page with a clear audience |
| Implementer | How do I wire it up? | `getting-started.md`, plus parts of `connectivity.md` and `delivery.md` |
| Reference reader | What exactly does a `202` mean? What is the default TTL? | The tables in `delivery.md`, `responses.md`, `events.md` |
| Deep-diver | Why is it like this? | Scattered through every page as blockquotes and asides, plus `decisions/` |

`connectivity.md` is the clearest case and the worst one. It is 311 lines, third in **Start
here**, and it opens by telling a .NET MAUI reader they probably do not need to read it — then
puts the thing they *do* need 80 lines down, behind a 100-line code block, five notes on Rx and
platform threading, a Windows API discussion and an analysis of DNS negative caching. Every one
of those is worth having. None of them is what the reader came for.

The same shape recurs: `delivery.md`'s "Understanding TTL" is the clearest writing in the docs
and sits 40 lines below the tables it explains. `events.md`'s best statement of the design — a
write is submitted, not successful — is behind a collapsed `<details>` element on a reference
page.

## Proposed shape

Four kinds of document, in the order a reader meets them.

**1. Tutorial** — new, `docs/tutorial/`, a few pages with next/previous links, building one small
app end to end. Each page ends with what the reader has just proved and a link onward.

1. A read that works offline — register, `GET`, turn the network off
2. A write that survives — `POST` offline, read the `202`
3. Finding out what happened — events and the correlation id
4. Telling Hyperwyc about your network — connectivity, and why it is a decision
5. Varying it per route — policies and TTL

The tutorial is where "install it, register it, and make a request" actually gets fulfilled;
`getting-started.md` currently promises that and has no request in it.

**2. Quick starts** — short, copy-paste, one per host. `docs/quickstart/maui.md` is the important
one and is the reason [57](../57-plugin-maui-hyperwyc.md) exists: MAUI is the core use case and it
currently has no page of its own. One page carrying the connectivity implementation, the
`SecureStorage` key ([32](../32-default-encryption-key.md)), backup exclusion
([48](48-exclude-store-from-os-backup.md)) and the AOT caveat ([53](../53-aot-json-serialization.md))
is worth more than four cross-references.

**3. Reference** — the existing pages, stripped to what the library does. Tables, defaults,
signatures, exact behaviours. No essays.

**4. Explanation** — two pages, not one, because they are different commitments:

- **Design principles.** The stances: infrastructure is not application logic; a queued write is
  submitted rather than successful; why `null` and not an empty body; why a `200` and not a
  `404`; why no retry; why no deduplication. A reader can disagree with all of it and still use
  the library correctly.
- **Patterns.** What to actually build: the application-owned store, correlating a `202` to a
  local record, marking unsynced then synced from the event stream, a "sync now" affordance.
  **This is [item 50](../50-resilient-applications-guide.md)**, already specified down to the
  inspection-app worked example. Patterns is not a new document; it is 50 finally being written,
  and this item is the argument for scheduling it.

Plus a glossary, filed separately as [60](../60-glossary.md).

## The sharpest thing to come out of the review

From the author's TODO in `delivery.md`, and it belongs in the principles page close to verbatim:

> the premise that "you don't have to change your calling code" no longer holds true [for an app
> that catches an HTTP client exception in a ViewModel]

The promise is on the front page and it is the library's headline. It stops being true for an
application handling infrastructure failures in application code — precisely *because* Hyperwyc's
whole point is that the exception stops happening. That is an opinion, it is defensible, and
stating it plainly makes it easier to accept rather than harder. Burying it makes the headline
look careless instead of deliberate.

The related thread, which does not appear anywhere yet: Hyperwyc nudges an application toward
distributed-systems patterns — requests as accepted rather than completed, with the event stream
as the eventual-consistency channel. That is the through-line making the `202`, the correlation
id, the event stream and the "no data" read one design instead of four decisions.

## Smaller structural fixes, worth doing regardless

- **One name per document.** `delivery.md` is called "Caching and delivery", "Caching and route
  policies" and "Delivery and Route Policies" in three places.
- **`responses.md` is missing from the README's documentation list.**
- **The false-positive/false-negative asymmetry is written out in full six times** — three times
  in `connectivity.md` alone, plus `responses.md`, `offline-writes.md`, `HyperwycOptions`' XML
  and ADR 0007. Once, at length, linked from the rest.
- **`connectivity.md`'s closing paragraphs on `AlwaysOnlineConnectivityService`** sit inside the
  DNS section and belong beside the one-line bullet that describes the type 200 lines earlier.
- **"Faking connectivity in your own tests"** serves a fourth reader again and is the seed of a
  short testing page, which could also absorb the `ReplayTransport`-as-a-stub note from
  `pipeline.md`.
- **The append-only / shared-mutable fit test appears three times** (README, `choosing.md`, and
  item 50, which specifies it). It is the best idea in the docs and repetition dilutes it.

## What shipped

| Document | |
|---|---|
| **[`docs/tutorial/`](../../docs/tutorial/)** | Five pages with previous/next links, building a console app that keeps working when its API goes away. Reads → writes → outcomes → connectivity → policies |
| **[`docs/maui.md`](../../docs/maui.md)** | The MAUI quick start. Registration, the connectivity implementation, the `SecureStorage` key ([32](../32-default-encryption-key.md)), backup exclusion ([48](48-exclude-store-from-os-backup.md)) and the AOT caveat ([53](../53-aot-json-serialization.md)) on one page. It opens by telling the reader they can skip `connectivity.md` |
| **[`docs/design.md`](../../docs/design.md)** | The explanation layer. Connectivity as an optimisation, why not to probe, why a `200` and not a `404`, why `null`, why no retry, no position on duplicates, and the calling-code caveat |
| **[`docs/testing.md`](../../docs/testing.md)** | Faking connectivity, stubbing the replay transport, a store per test — and the point that stopping the API tests the real path rather than a mocked one |
| **[`docs/README.md`](../../docs/README.md)** | Rebuilt as **Start here / Reference / Why it works this way**, with the start-here row chosen by why the reader came |

Stripped back to reference: `connectivity.md` (337 → 146), `delivery.md`, `events.md`,
`responses.md`, `offline-writes.md`, `choosing.md`, `pipeline.md`. Every essay they were carrying
moved to `design.md`, `maui.md` or `testing.md` rather than being deleted.

`getting-started.md` was rewritten and now actually makes a request, which the first line had been
promising and not delivering.

## The tutorial is verified, not asserted

Written after running it, not before. A console app was built against a local `ProjectReference`
to `Hyperwyc`, with a minimal API started and stopped in-process, and the whole arc executed:

```
=== 1. ONLINE ===      event: OnUpdated GET /products
                       GET  /products -> 2 products, first=Anvil
=== 2. OFFLINE ===     GET  /products -> 2 products, first=Anvil       (from the store, no throw)
                       GET  /nothing-cached -> 200 X-Hyperwyc-Status=Offline body=null
                       event: OnQueued POST /sales correlation=28b49b97…
                       POST /sales -> 202 Queued correlation=28b49b97…
=== 3. BACK ONLINE ===  event: OnDelivered POST /sales outcome=Succeeded/201
```

**Doing that changed the tutorial's spine.** Stopping the API does not make the connectivity
service report offline — it reports connected, correctly, because the machine's network is fine.
So every offline read and queued write in pages 1–3 happens on the *transport-failure* path, which
is [ADR 0007](../../docs/decisions/0007-connectivity-cannot-cost-correctness.md)'s behaviour rather
than the connectivity service's.

That is a better narrative than the one planned. Pages 1–3 demonstrate that the zero-config path
works and *why* it works, and page 4 then arrives as "everything so far ran without you telling
Hyperwyc anything about the network — here is what that cost you." Had the tutorial been written
from the design rather than from a run, it would have claimed the connectivity service was doing
work it was not.

## Acceptance Criteria

- [x] A reader can get a working MAUI app from one page without reading `connectivity.md`.
- [x] Every reference page can be skimmed for a fact without reading an argument.
- [x] Every stance the library holds is somewhere a reader can find it deliberately, rather than
      by encountering it inside a page about something else.
- [x] One name per document, used in every link to it — `delivery.md`'s H1 is now
      "Caching and route policies", which is what the nav calls it.
- [x] Every link and anchor across `docs/`, `Backlog/` and the README resolves.

- The author reached this from two directions independently, in `docs/events.md` ("a clear
  separation between what consumers need to read for it to be useful vs what people deep-diving
  need") and `docs/delivery.md` ("an opinions doc, or similar — philosophy maybe"). Both TODOs
  should be deleted by this item rather than answered in place.
- This is close to the Diátaxis split (tutorial / how-to / reference / explanation) arrived at
  from the symptoms rather than from the framework. Worth knowing the prior art exists; not worth
  adopting the vocabulary, which would be a fifth thing to explain.
## Sequencing

The first draft of this item said "fix the accuracy work first, moving wrong text is wasted
motion". That is too simple, and it is only true of some of it.

**[61](61-xml-docs-contradict-the-code.md) goes first regardless.** It is a different artefact —
XML that ships inside the package — so nothing here touches it, and it is the one finding with a
live wrong-behaviour consequence attached (`HyperwycOptions.Routes` documents the opposite
matching order to the one the code implements).

After that it depends on whether the first publish comes before or after the restructure, which
is a scheduling decision rather than a technical one:

- **Publishing first.** [58](58-docs-code-reconciliation.md) and
  [59](59-events-without-system-reactive.md) land against the pages as they stand, and this item
  follows. Shipping unstructured documentation is survivable; shipping documentation that names
  an enum member which does not exist is not.
- **Restructuring first.** Do this item's *skeleton* — decide the page set and where each thing
  lives, not the writing — then apply 58 into the new shape.

The reason the second is not obviously worse: **roughly half of 58's findings are in text this
item moves or deletes.** The clearest case is the false-positive/false-negative asymmetry, which
58 records as "one of the six copies is wrong" and this item resolves as "there should be one
copy". Correcting a passage you are about to delete is the wasted motion, not the other way
round.

[59](59-events-without-system-reactive.md) now depends on this item rather than blocking it — the
question is no longer which form to show but which page each form belongs on, and that is
answered here. [60](../60-glossary.md) is independent of both and can be done at any point.

## Notes

- **Patterns was not written, and that is deliberate.** This item argued the explanation layer is
  two pages — principles and patterns — and that the second is
  [50](../50-resilient-applications-guide.md) finally being written. Only principles shipped, as
  `design.md`. Writing 50 as a side effect of a restructure would have produced a worse version of
  a document that is already specified in detail, including a worked example this item does not
  have. `design.md` ends its "submitted, not successful" section by naming the gap.
- **The glossary ([60](../60-glossary.md)) was also left alone** for the same reason: separately
  filed, independently useful, and one line of navigation to add when it lands.
- **Running the tutorial before writing it was the highest-value hour of this item.** It corrected
  a claim about which mechanism was doing the work, and it means every output in those five pages
  is a transcript rather than an expectation. Worth repeating for any future guide.
- Comments and TODOs left in the docs now belong to open items only —
  [59](59-events-without-system-reactive.md) on the Rx snippet,
  [60](../60-glossary.md) on dead-lettering, [62](../62-reset-store-on-failure.md) on store reset.
