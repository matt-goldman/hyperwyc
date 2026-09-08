# Issue 62 — Should an Unreadable Store Reset Itself?

## Summary

Author's question in `docs/storage.md`: should `HyperwycOptions` carry something like
`ResetStoreOnFailure = true`, so that a store which cannot be read is discarded and recreated
rather than leaving Hyperwyc in pass-through for the session?

## Status

💭 Under consideration. Filed 2026-09-08. **Recommendation: no**, with a documented alternative.

## The case against

**It is defaults test question 3.** The outbox is a list of writes that have not happened yet.
`ResetStoreOnFailure = true` means "if the store cannot be read, silently discard writes the
caller was told had been accepted" — quietly producing the failure the library exists to prevent.
It is also wrong-and-silent by construction: the consumer finds out because the data is not
there.

**It fires in the case where the data is most likely intact.** The usual cause of an unreadable
store is a key or a path change, not corruption — see
[item 49](Done/49-unreadable-store-recovery.md), and [item 48](48-exclude-store-from-os-backup.md)
for the iOS restore case where the derived key stops matching. The bytes are fine and merely
unreadable, and the fix is often to restore the key rather than to destroy the store. Deleting on
the first failed read forecloses that, irreversibly, on the reading that is most likely wrong.

**Item 49 already settled the shape.** Report it, get out of the way, and give the application a
method to call once it has decided. That decision needs things Hyperwyc does not have: whether
the application keeps its own record of the pending writes, whether the user should be told,
whether this is a fresh install or a restore onto a new device. 49's first draft had Hyperwyc
either recreate the store or refuse to start, and both were rejected as out of scope. This is the
first of those two coming back as an option.

## The smaller answer, if the complaint is ergonomics

Not an option — a documented three lines, in the consumer's code, where the decision belongs:

```csharp
hyperwyc.Events
    .Subscribe(e => { if (e.Type == HyperwycEventType.OnStoreUnreadable) _ = hyperwyc.ResetStoreAsync(); });
```

Same behaviour, opted into explicitly, and visible in a code review in a way a `true` in a
configuration block is not. It belongs on `docs/storage.md` next to `ResetStoreAsync`.

(Note the snippet needs whatever [59](59-events-without-system-reactive.md) settles about
consuming events without Rx.)

## Acceptance Criteria

- [ ] `docs/storage.md` records this as a deliberate absence with the reasoning, rather than
      leaving the question open.
- [ ] The subscribe-and-reset pattern is documented as the supported way to get the behaviour.
- [ ] If the answer turns out to be yes after all, it is an ADR — it reverses a conclusion in
      item 49.

## Notes

- Filed because the question was asked, not because the answer is in doubt. The value of the item
  is that the next person to want this finds the reasoning instead of the gap.
