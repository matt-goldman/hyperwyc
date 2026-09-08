# Issue 59 — The Documented Way to Consume Events Requires a Package Hyperwyc Refuses to Take

## Summary

`docs/events.md` shows the primary example of reading a delivery outcome as an Rx query:

```csharp
hyperwyc.Events
    .Where(e => e.Type == HyperwycEventType.OnFailed)
    .Subscribe(...);
```

`.Where` over `IObservable<T>` is `System.Reactive.Linq`. `HyperwycEventStream` is a hand-rolled
`IObservable<HyperwycEvent>` with no operators on it, and Hyperwyc takes no `System.Reactive`
dependency by design. So the documented snippet does not compile in a consumer application
unless they have added Rx themselves, and nothing on the page says so.

## Status

⬜ Open. Filed 2026-09-08, from the author's own TODO on the snippet. **Blocks first publish** —
it is the main example on the page.

## Why it is worse than an omission

It is a trap in a specific direction. `docs/connectivity.md` makes a point of the no-Rx decision
twice: once in the five notes on `MauiConnectivityService` ("a package reference existing for one
field"), and again in the testing section. A reader who takes that advice and hand-rolls their
own observable, then turns to `events.md` for the outcome-handling example, hits a compile error
the documentation walked them into.

## Options

1. **Note the Rx dependency and move on.** Honest, one sentence. But it makes the documented path
   depend on a package the library declines to take, which reads as inconsistency even when it is
   defensible — the consumer's dependency graph is theirs, not Hyperwyc's.
2. **Show the plain `IObserver<HyperwycEvent>` form.** A few more lines, no dependency, and
   closer to what most consumers will actually write. **Preferred.** Keep the Rx form as a
   secondary example marked as requiring Rx, since plenty of consumers do already have it.
3. **Ship the operators.** Fails the scope test at question 1 — the problem exists without
   Hyperwyc, and it has an owner called `System.Reactive`. Not worth relitigating.

## Adjacent

Once the observer form is written, the same snippet needs the correction from
[58](58-docs-code-reconciliation.md): its `else` branch is unreachable, because `OnFailed` is
only published from `DeadLetterAsync` and a transport failure publishes no event at all.

Worth doing both in one pass — the branch is what the example is mostly demonstrating, so
rewriting it without correcting it would just port the error.

## Acceptance Criteria

- [ ] The primary example on `docs/events.md` compiles against `Hyperwyc` alone.
- [ ] Any Rx example is labelled as needing Rx.
- [ ] The unreachable branch is gone, and the page says plainly that silence is what a transport
      failure looks like from the event stream.

## Notes

- Worth checking `README.md` and `docs/` generally for the same shape before closing: any snippet
  using an `IObservable` operator has the same problem.
