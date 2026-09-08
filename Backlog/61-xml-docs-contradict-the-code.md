# Issue 61 — XML Documentation Contradicts the Code, and Ships in the Package

## Summary

Several public members carry XML documentation describing behaviour that was changed or removed.
Unlike `docs/`, this text ships inside the NuGet package and is what IntelliSense shows a
consumer at the moment they are writing the line it is wrong about.

## Status

⬜ Open. Filed 2026-09-08. **Blocks first publish** — the first finding will silently produce
the wrong route policy for anyone who follows it.

## Findings

### 1. `HyperwycOptions.Routes` documents the opposite matching order

```csharp
/// Register most specific first — see <see cref="RoutePolicyMap"/>.
/// Per-route policies, matched first-registered-wins ...
```

`RoutePolicyMap.PolicyFor` walks the list **backwards**. Last registration wins; you register
general to specific, each rule refining the ones before it, which is the `.gitignore` and CSS
cascade model the type's own remarks describe at length.

So `HyperwycOptions` tells a consumer to do the exact opposite of what works, on the property
they configure, and getting it wrong is silent — the request is served under a policy that
matched, just not the one they meant.

`RoutePolicy`'s own summary says "Policies are matched to routes by `RoutePolicyMap`, first match
wins", which is true of a backwards walk and reads as the opposite. Worth rewording even though
it is not strictly false.

### 2. `HyperwycOptions.ReplayTransport` contradicts ADR 0002

```csharp
/// Replayed requests do not travel through your application's HttpClient pipeline —
/// they are sent directly, bypassing HyperwycHandler ...
```

True before [item 37](Done/37-replay-through-pipeline.md), and now the opposite of
[ADR 0002](../docs/decisions/0002-replays-traverse-the-pipeline.md) and of `docs/pipeline.md`,
whose central claim is that a replay goes back through the pipeline it was made on so a write
queued on Monday gets Tuesday's token.

It is the **fallback** that bypasses the pipeline — a handler registered without a client name —
which is what this property is for. The text describes the fallback as though it were the rule.

### 3. Leftovers from the ADR 0004 retry removal, in `OutboxProcessor`

Internal, so not consumer-facing, but they describe a mechanism that no longer exists and will
mislead the next person to read the file:

- `FlushAsync` remarks: "An envelope that fails transiently is left queued **with a scheduled
  next-attempt time**" — there is no scheduling and no transient class.
- `SendOutcome.DeadLettered`: "Rejected **or out of budget**" — there is no budget.
- `ResetStoreAsync` remarks reference "the scheduled follow-up" as something the processor owns.
- A dangling `/// <summary>Ceiling on a computed backoff ...</summary>` with no member beneath
  it, left attached to the field declarations.

### 4. `IHyperwyc.FlushAsync` overstates what happens next

"Queued writes that cannot be delivered stay in the outbox and are **retried on the next flush**"
— true, but "retried" implies a schedule. There are three triggers and none of them is a timer.

### 5. `NetworkAvailabilityConnectivityService` carries a corrected claim

"Unlike the cases above, that is not a degraded network path; **it is not a path at all**",
about a VPN or mesh interface holding `GetIsNetworkAvailable()` at `true`.

This was corrected once already: where the API is inside the tunnel — enterprise, IoT — that
tunnel is the only path that matters and reporting connected is right. The correction landed and
has since come back in both the XML and `docs/connectivity.md`. The honest version is that
whether it is wrong depends on where the API lives, which is the thing Hyperwyc cannot know — and
that makes it the best available illustration of why nothing is registered by default rather than
a caveat.

## Acceptance Criteria

- [ ] `HyperwycOptions.Routes` and `RoutePolicy` describe last-registration-wins, and agree with
      `RoutePolicyMap` and `docs/delivery.md`.
- [ ] `ReplayTransport` describes itself as the fallback transport, and points at ADR 0002 for
      what replays normally do.
- [ ] No XML comment names a mechanism removed by ADR 0004.
- [ ] The VPN claim is stated once, correctly, in both the XML and the docs.

## Notes

- **Finding 1 is the reason this is not filed inside [58](58-docs-code-reconciliation.md).** That
  item is about `docs/`, which can be fixed after publishing. This ships in the package, and a
  consumer reading IntelliSense has no reason to go and check the docs, because the two are meant
  to agree.
- Finding 3 is the same class of thing the ADR 0004 audit was itself about: a decision to remove
  something orphans the apparatus around it, and nothing goes back to collect it unless someone
  looks. The audit collected the orphaned code and left the orphaned prose.
- **Worth a lint.** Several of these would have been caught by anything that diffed public XML
  against the docs, or even by generating the XML file and reading it once. Not proposing a tool;
  proposing that the XML gets read as a document before the first publish.
