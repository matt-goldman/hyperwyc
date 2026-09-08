# Issue 61 — XML Documentation Contradicts the Code, and Ships in the Package

## Summary

Several public members carry XML documentation describing behaviour that was changed or removed.
Unlike `docs/`, this text ships inside the NuGet package and is what IntelliSense shows a
consumer at the moment they are writing the line it is wrong about.

## Status

✅ Done, 2026-09-08. Seventeen corrections across seven files. Builds clean with
`-warnaserror:CS1574,CS1584,CS1658,CS1572,CS1573`, and the suite is unchanged at 332 passing.

**Five findings beyond the five filed** were turned up by reading the generated XML as a
document — see [What the read-through added](#what-the-read-through-added). That is the part of
this item worth remembering.

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

True before [item 37](37-replay-through-pipeline.md), and now the opposite of
[ADR 0002](../../docs/decisions/0002-replays-traverse-the-pipeline.md) and of `docs/pipeline.md`,
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

## What the read-through added

The five filed findings came from checking claims the docs review had already flagged. These five
came from generating `Hyperwyc.Core.xml` and reading every public summary in order, which took
about ten minutes and found as much again.

| Member | What it said | What is true |
|---|---|---|
| `IHyperwycStore.MoveToDeadLetterAsync` | "after all retry attempts have been exhausted" | There is no retry budget. An envelope is dead-lettered as soon as the server answers with a non-success status |
| `Envelope.IsDeadLettered` | "after exhausting all retry attempts" | Same |
| `IHyperwycStore.MarkDeliveredAsync` | "marks ... as successfully **synced**" | Pre-vocabulary-pass wording. ADR 0005 renamed the method from `MarkSyncedAsync`; its summary did not follow |
| `HyperwycEventType.OnDelivered` | "successfully **synced** to the server" | Same |
| `Envelope.IsSynced` | "whether the request has been successfully synchronised (sent to the remote server) at least once" | **Actively false** on a cached response, which carries `true` from creation having been sent nowhere. This is [55](../55-envelope-kind-discriminator.md)'s whole point, and the summary was asserting the thing 55 says is untrue |

The first two are on `IHyperwycStore`, which is the interface a consumer implements when they
supply their own store — so it is arguably the most consequential XML in the package, and it was
describing a retry model deleted a fortnight earlier.

`Envelope.IsSynced` now documents what the flag actually does — excludes an envelope from the
outbox — says plainly that the name is wrong, and points at 55. **Deliberately not renamed**: 55
owns the model change, and 55's own note says not to fold it into a rename.

## Acceptance Criteria

- [x] `HyperwycOptions.Routes` and `RoutePolicy` describe last-registration-wins, and agree with
      `RoutePolicyMap` and `docs/delivery.md`.
- [x] `ReplayTransport` describes itself as the fallback transport, and points at ADR 0002 for
      what replays normally do.
- [x] No XML comment names a mechanism removed by ADR 0004.
- [x] The virtual-interface claim is stated once, correctly, in the XML — **on the second
      attempt**; see the note below. `docs/connectivity.md` still carries the old sentence and is
      annotated with the correction, to be fixed in [58](../58-docs-code-reconciliation.md) so
      the docs pass stays in one place.

## Notes

- **Finding 1 is the reason this is not filed inside [58](../58-docs-code-reconciliation.md).** That
  item is about `docs/`, which can be fixed after publishing. This ships in the package, and a
  consumer reading IntelliSense has no reason to go and check the docs, because the two are meant
  to agree.
- Finding 3 is the same class of thing the ADR 0004 audit was itself about: a decision to remove
  something orphans the apparatus around it, and nothing goes back to collect it unless someone
  looks. The audit collected the orphaned code and left the orphaned prose.
- **Reading the generated XML found as much as the targeted search did.** Grepping for known
  stale terms — `budget`, `backoff`, `transient`, `scheduled` — found the five filed findings and
  missed five more, because the remaining ones used no distinctive vocabulary: "successfully
  synced", "retry attempts have been exhausted" and "successfully synchronised" are only wrong if
  you know what the code now does. **Generate the XML and read it in order before publishing.**
  It is a different artefact from the source and it reads differently — a summary that looks fine
  above a method reads as a claim when it is one line in a list of two hundred.
- **The vocabulary pass (ADR 0005) left residue in the XML too.** It renamed `MarkSyncedAsync` to
  `MarkDeliveredAsync` and `OnSynced` to `OnDelivered`, and both kept summaries saying "synced".
  Same shape as the ADR 0004 leftovers and the same lesson: a rename that changes an identifier
  does not change the prose around it, and nothing fails when it disagrees.
- **`Envelope.IsSynced` was documented as doing the thing [55](../55-envelope-kind-discriminator.md)
  exists to say it does not do.** 55 records that the flag is named after something it is not;
  the XML went further and asserted the false meaning as fact. Corrected in place, not renamed —
  55 still owns the model.
- **Finding 5 was corrected twice and then deleted.** The shipped text explained at length how a
  VPN or mesh interface can hold `GetIsNetworkAvailable()` at `true`. The first pass rewrote it to
  "whether that is wrong depends on where your API is — if it is inside the tunnel, reporting
  connected is correct", which is incoherent: a tunnel carries its packets over the physical link,
  so with the link down it delivers nothing whatever it reports. The second pass corrected that
  back, with a measurement. The third removed the whole thing under
  [ADR 0004](../../docs/decisions/0004-default-to-removal.md).

  **That is the finding.** Two rounds of careful correction on a passage that failed the removal
  test, and neither round asked whether it should be there — the question "is this right?" crowded
  out "does this earn its space?". ADR 0007 had already made the taxonomy irrelevant: a false
  positive costs an attempt, so enumerating *which* false positives exist is machinery around the
  promise. The preceding sentence — captive portal, no upstream, weak signal — already establishes
  the category and covers what people actually hit. What survived is one clause, moved to the
  "write your own" guidance: if reaching your API depends on something narrower than "a network",
  write an implementation that watches that.

- **The measurement is worth keeping even though the prose went.** On a Linux host `tailscale0`
  reports `NetworkInterfaceType.Unknown` and `OperationalStatus.Up`, and is counted — it is `Up`
  because the kernel reports `operstate` as `unknown` and **.NET maps that to `Up`, so .NET is
  more permissive than the OS is**. Container and hypervisor bridges report as `Ethernet` and are
  not excluded either. Recorded here rather than in the XML because it is the kind of thing that
  gets rediscovered, and one line in a `Done/` item is the right price for it.
