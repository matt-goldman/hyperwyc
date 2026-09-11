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

✅ Done, 2026-09-09. **Two snippets were affected, not one.** `Subscribe(Action<T>)` is also an Rx
extension — `IObservable<T>` declares only `Subscribe(IObserver<T>)` — so the page's opening
one-liner, the "here is how you subscribe" example, did not compile either. Confirmed rather than
assumed:

```
error CS1660: Cannot convert lambda expression to type 'IObserver<HyperwycEvent>'
              because it is not a delegate type
```

Both are fixed. The opening example is a plain `IObserver<HyperwycEvent>`; the outcome-handling
example keeps its Rx form, labelled, because that is what most applications will want.

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

The same snippet needs the correction from [58](58-docs-code-reconciliation.md): its `else`
branch is unreachable, because `OnFailed` is only published from `DeadLetterAsync` and a
transport failure publishes no event at all.

Do both in one pass. The branch is most of what the example demonstrates, so rewriting it in
either form without correcting it just ports the error into two places instead of one.

## Acceptance Criteria

- [x] The primary example on `docs/events.md` compiles against `Hyperwyc` alone.
- [x] The Rx form survives somewhere a reader will find it, labelled as needing Rx.
- [x] The unreachable branch is gone, and the page says plainly that silence is what a transport
      failure looks like from the event stream.
- [x] Nothing elsewhere in `docs/` or the README uses an `IObservable` operator without saying so
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

- **The acceptance criterion about "nothing elsewhere" is what found the second one.** Checking it
  properly meant compiling the snippets rather than reading them, and the opening example turned out
  to be the worse of the two: a reader hits it before anything else on the page.
