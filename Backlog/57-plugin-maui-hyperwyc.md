# Issue 57 — `Plugin.Maui.Hyperwyc`

## Summary

A .NET MAUI package carrying `MauiConnectivityService`, taking a dependency on Hyperwyc, and
wiring both up through one extension method on `MauiAppBuilder`. Author's TODO in
`docs/connectivity.md`.

## Status

💭 Under consideration. Filed 2026-09-08.

## Why

MAUI is the core use case and it currently has the worst onboarding path in the docs. A MAUI
developer is told to copy a 100-line class out of a 311-line page, paste it into their app, and
register it — and the reason it is not packaged, that it would put MAUI in the dependency graph
of every console app and Blazor host, is a Hyperwyc problem being solved in the consumer's
codebase. A separate package solves it where it belongs.

The same package is the natural home for the other three things a MAUI app needs and currently
collects from four different pages: the `SecureStorage`-backed encryption key
([32](32-default-encryption-key.md)), backup exclusion
([48](Done/48-exclude-store-from-os-backup.md)), and whatever [53](53-aot-json-serialization.md)
concludes about AOT.

## The decision this actually asks for

**It is an ADR-shaped question, not a packaging one**, and it should not be built without
answering it.

[ADR 0006](../docs/decisions/0006-a-shipped-implementation-is-not-a-default.md) says a shipped
implementation is not a default: shipping something to point at is how a required decision is
made cheap, and registering it automatically is how the decision disappears. A meta extension
method on `MauiAppBuilder` that wires up a connectivity source **is** making that choice on the
consumer's behalf — the first time Hyperwyc would do so.

That is probably right, and the defaults test says why. Question 1 is "can Hyperwyc choose
correctly from what it knows?" — on MAUI, with a MAUI-specific package, the answer is yes in a
way it is not in the core package, because the platform is no longer unknown.
`Connectivity.Current` is the right answer on every host that package can be installed on, which
is exactly the condition question 5 asks about.

But two things follow that need deciding rather than assuming:

1. **`ConstrainedInternet`.** The sample treats it as disconnected; the docs say to flip it if
   your API is reachable behind a captive portal. A package has to pick one and expose the other,
   which is a small API surface decision with an ADR 0003 shape.
2. **Whether the meta method is opt-in.** `AddHyperwycForMaui()` registering connectivity is
   different from `AddHyperwyc()` doing it, and only the second contradicts 0006. Keeping the
   MAUI-specific call explicit is probably the whole answer.

## What it would contain

- `MauiConnectivityService` — the existing sample implementation, unchanged
- `AddHyperwycForMaui()` or similar on `MauiAppBuilder`, registering the handler, the store and
  connectivity together
- Optionally the `SecureStorage` key provider from [32](32-default-encryption-key.md)
- Optionally the backup-exclusion call from [48](Done/48-exclude-store-from-os-backup.md), which on
  iOS is a one-line `NSUrl` call at startup and has no good home in a consumer's code either

## Acceptance Criteria

- [ ] The ADR question is answered before any code: does a platform-specific package get to
      register a connectivity source, and does that hold 0006 or amend it?
- [ ] A MAUI app gets working offline behaviour from one package reference and one line.
- [ ] Nothing in the core package changes as a result.
- [ ] The docs stop asking a MAUI reader to copy a class, or explain when they still should.

## Notes

- **Item 13 is the cautionary precedent and should be read first.** It never shipped a line of
  library code across three scope revisions — ship `MauiConnectivityService` from core, then ship
  a test double, then documentation only, then documentation with no default at all — each pass
  removing something from the package. This item proposes shipping the type that item decided
  three times not to ship. The difference is real (a separate package has no cost to non-MAUI
  consumers, which was the objection every time) but it deserves stating explicitly rather than
  being rediscovered.
- `docs/connectivity.md` currently says "the sample implementation is almost certainly never
  going to change, so owning it yourself isn't a risk". That is an argument against this item and
  it is a decent one. It also stops being true the moment the implementation has a version number.
  Both statements cannot survive this item.
