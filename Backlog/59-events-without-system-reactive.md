# Issue 59 — The Documented Way to Consume Events Requires a Package Hyperwyc Does Not Take

## Summary

`docs/events.md` shows the primary example of reading a delivery outcome as an Rx query:

```csharp
hyperwyc.Events
    .Where(e => e.Type == HyperwycEventType.OnFailed)
    .Subscribe(...);
```

`.Where` over `IObservable<T>` is `System.Reactive.Linq`. `HyperwycEventStream` is a hand-rolled
`IObservable<HyperwycEvent>` with no operators on it, and Hyperwyc takes no `System.Reactive`
dependency by design. So the snippet does not compile against `Hyperwyc` alone, and nothing on
the page says so.

## Status

⬜ Open. Filed 2026-09-08, from the author's own TODO on the snippet. **Blocks first publish** —
it is the main example on the page.

## Not a case for removing the Rx version

The first framing of this item was "show the plain observer form instead". That is wrong, or at
least half of it is: **Rx belongs in most UI applications**, and a MAUI or WPF consumer reaching
for it is doing the right thing, not taking on an unnecessary dependency. Hiding the version most
people should ideally use, in order to keep a snippet dependency-free, trades the better advice
for the more portable one.

The two forms have different readers, which is a placement question rather than a choice:

| Form | Where it belongs | Why |
|---|---|---|
| Plain `IObserver<HyperwycEvent>`, `switch` inside `OnNext` | `docs/events.md` — reference | Compiles against `Hyperwyc` alone. Shows the event shape without asking anything of the reader's project. This is the page that documents what the library emits |
| Rx query | **Patterns** — see [56](56-documentation-restructure.md) | This is how you actually wire outcomes into a view model, which is a pattern rather than a fact about the library. It sits alongside correlating a `202` to a local record and marking unsynced then synced |

That split is a small argument for the restructure rather than against it: the reason the current
page is wrong is that it is trying to be reference and pattern at once, and the pattern half
carries a dependency the reference half must not.

Shipping the operators is the third option and still fails the scope test at question 1 — the
problem exists without Hyperwyc and has an owner called `System.Reactive`.

## Adjacent

The same snippet needs the correction from [58](Done/58-docs-code-reconciliation.md): its `else`
branch is unreachable, because `OnFailed` is only published from `DeadLetterAsync` and a
transport failure publishes no event at all.

Do both in one pass. The branch is most of what the example demonstrates, so rewriting it in
either form without correcting it just ports the error into two places instead of one.

## Acceptance Criteria

- [ ] The primary example on `docs/events.md` compiles against `Hyperwyc` alone.
- [ ] The Rx form survives somewhere a reader will find it, labelled as needing Rx.
- [ ] The unreachable branch is gone, and the page says plainly that silence is what a transport
      failure looks like from the event stream.
- [ ] Nothing elsewhere in `docs/` or the README uses an `IObservable` operator without saying so
      — the same shape may exist in more than one place.

## Notes

- `docs/connectivity.md` makes a point of the no-Rx decision twice, in the notes on
  `MauiConnectivityService` and again in the testing section. Those are about **Hyperwyc's**
  dependency graph, not the consumer's, and the pages currently blur the two — a reader can
  come away thinking Rx is discouraged for them, which is not the position. Worth a clause
  there while this is being fixed.
- The snippet in [62](62-reset-store-on-failure.md)'s earlier draft had the same problem, which
  suggests the Rx form is simply what gets reached for when writing an example. That is the
  argument for having a correct one on the page rather than none.
