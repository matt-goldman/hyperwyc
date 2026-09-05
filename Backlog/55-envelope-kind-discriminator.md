# Issue 55 — `Envelope.IsSynced` Distinguishes Two Kinds of Envelope by Negation

## Summary

`Envelope` is two things wearing one type: a cached response, and a queued write. They are told
apart by `IsSynced`, which is set `true` on a cached response that was never "synced" anywhere.
The flag does not mean what it says, and the vocabulary pass could not rename it because there is
no honest name for what it currently does.

## Status

⬜ Open. Filed 2026-09-05, surfaced by the vocabulary rename.

## What it does today

```csharp
// Envelope.ForCachedResponse
envelope.IsSynced = true;
```

```csharp
// InMemoryStore.GetPendingOutboxAsync
.Where(e => !e.IsSynced && !e.IsDeadLettered)
```

So `IsSynced` means *"not a pending outbox entry"*, and the outbox is defined by negation. A
cached `GET` response is marked synced because it is not a queued write, which is true and says
nothing about synchronising.

The rename pass renamed `MarkSyncedAsync` to `MarkDeliveredAsync` and `OnSynced` to `OnDelivered`
— both are genuinely about delivery — but deliberately left `IsSynced` alone. Renaming it
`IsDelivered` would have made it *worse*: a cached response was never delivered anywhere, so the
new name would be actively false where the old one was merely vague.

## Why it matters beyond naming

Two kinds of record sharing one type means every consumer of `Envelope` has to know which kind it
is holding, and nothing in the type says. `RequestBody` and `ClientName` are meaningless on a
cached response; `Response` and `CachedAt` are meaningless on a queued write; `CorrelationId` is
meaningful only on a write. A reader cannot tell which fields are load-bearing without knowing
the kind, and the kind is inferred from a boolean named after something else.

It also made the [issue 25](Done/25-binary-request-response-bodies.md) cache-identity bug harder
to see: cache envelopes and outbox envelopes share an id space, which is why a deterministic
`cache:` prefix was needed to keep them from colliding.

## Candidate shapes

1. **An explicit discriminator** — `Envelope.Kind` of `EnvelopeKind { CachedResponse, QueuedWrite }`.
   Smallest change, makes the intent readable, leaves the irrelevant fields present but
   explicable.
2. **Two types.** `CacheEntry` and `OutboxEntry`, sharing whatever is genuinely common. Honest,
   and makes the irrelevant fields impossible rather than merely ignorable. Larger: `IHyperwycStore`
   currently has one `UpsertAsync`, and would need either two or a shared base.

2 is the better model and 1 is the cheaper step toward it. Prefer 2 unless it turns out to
complicate `IHyperwycStore` more than the clarity is worth.

## Acceptance Criteria

- [ ] Nothing distinguishes envelope kinds by negation of a misnamed flag.
- [ ] A reader can tell from the type which fields are meaningful.
- [ ] `IHyperwycStore` stays comprehensible — if the split makes the interface worse, say so and
      take option 1.

## Notes

- **Do not fold this into a rename.** It surfaced during one and was deliberately excluded:
  mixing a model change into a mechanical pass is how a rename hides a bug. That reasoning holds
  for whoever picks this up next to something else.
- Sequence after [49](49-unreadable-store-recovery.md) rather than before — 49 is a real defect,
  this is a clarity problem with no known failure attached.
