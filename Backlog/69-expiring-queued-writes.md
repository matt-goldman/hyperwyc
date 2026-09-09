# Issue 69 — Should a Queued Write Expire?

## Summary

Hyperwyc will hold an undelivered write forever. There is no age limit, no attempt limit, and no way to ask for one. A device offline for a month reconnects and faithfully sends a month-old order.

This is the question [66](66-dead-letter-store-fails-the-scope-test.md) makes askable. It was obscured while "dead-lettered" meant something else.

## Status

💭 Under consideration. Filed 2026-09-09.

## The term was misapplied, not misjudged

In a message bus, dead-lettered means **could not be delivered**. Hyperwyc was applying it to requests that *had* been — which is why it read as alarming and why no honest definition could be written for it.

Strip that usage and the word is free for its actual meaning, and the question underneath becomes visible: **a write Hyperwyc has been unable to deliver for long enough that nobody wants it any more.** That is dead-lettering, correctly used, and Hyperwyc currently has no concept of it at all.

The difference from a bus is that Hyperwyc is a mediator rather than a conduit — it owes the caller a response — but that bears on what happens *after* delivery, not on this.

## It passes the scope test, where the other one failed

Worth putting side by side, because the two look similar and come out opposite:

|                                                                                              | Question 4 — does it depend on something only Hyperwyc knows?                                                                    |
| -------------------------------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------- |
| Retaining a **response** after delivery ([66](66-dead-letter-store-fails-the-scope-test.md)) | **No.** It is an ordinary HTTP response. The application can keep it, filed under a correlation id it chose                      |
| Expiring a **request** before delivery                                                       | **Yes.** It depends on how long something has been sitting in the outbox — and "the contents of the outbox" is named in the test |

So this is not the previous idea returning under a better name. It is a different question that the previous idea was standing in front of.

## Attempts: no, and for a sharper reason than taste

An attempt count would be a poor proxy for the thing actually being asked about.

Hyperwyc makes **one attempt per envelope per flush**, and flushes happen on connectivity restoration — so a count of attempts is a count of times the device *regained* connectivity. That is a property of where the phone has been, not of the request or the server. Ten reconnects on a train journey and ten reconnects over ten days are the same number and completely different situations; age separates them and a count does not. If what is meant is "give up after long enough", measure time.

The counts would also not be comparable to each other. A transport failure **stops the flush**, so the envelope at the front of the queue accrues an attempt and the ones behind it do not. Two writes queued in the same second could differ by a wide margin purely by position.

**Note what this argument is not.** It is not that connectivity services are chatty — both shipped implementations publish only on an actual transition, and `maui.md` tells anyone writing their own to do the same, precisely because MAUI raises `ConnectivityChanged` for changes that keep you online. A service that published spuriously would be an implementation error the docs already warn against, and an attempt budget defending against it would be armour for a bug we tell people not to write.

## Age: probably yes, and the docs already argue for it

`docs/storage.md`, on restoring a backup:

> A worse scenario is a non-duplicate request that no longer makes sense, especially if your API attaches a time received timestamp.

That hazard is already named as *worse than duplicates*, and nothing is offered against it. A restored backup is one way to reach it; a month offline is another, and needs no backup at all.

`Envelope.CreatedUtc` already exists and is already persisted, so this is a check and an event rather than a schema change.

## Constraints on any implementation

- **Off by default, and no default age.** How long is too long is entirely domain knowledge — an inspection report may be good for weeks, a price quote for minutes. [ADR 0003](../docs/decisions/0003-default-what-you-can-decide-correctly.md) question 1: Hyperwyc cannot choose this correctly, so it must not choose.
- **It must be loud.** Discarding a write is the failure this library exists to prevent. Expiry is the one place Hyperwyc would do it deliberately, so it has to raise an event saying which write and why. Silent expiry would be the worst behaviour in the library.
- **Do not reuse `RoutePolicy.Ttl`.** Tempting, since per-route configuration already exists there, but a TTL governs how old a stored *response* may be and still be served, and this governs how long a *request* may wait before being abandoned. Different units, different consequences — stale data versus destroyed work. One member meaning two things depending on the HTTP method is [55](55-envelope-kind-discriminator.md)'s mistake exactly. It wants its own member.
- **No scheduler.** Check age at flush, which is the moment you would otherwise have tried to send it. Consistent with there being three triggers and no timer, and it needs no new machinery.

## Expire and discard, or expire and keep?

Discard first. If the application owns its own record — the pattern the docs recommend — it already knows the write never went, and the event tells it why. The request body is also the most sensitive thing in the store, which argues against keeping it past the point of usefulness.

**But note what "keep" would mean.** A store of requests Hyperwyc *could not deliver* is a genuine dead-letter queue, and it would be the first artefact in this codebase that deserves the name. It is categorically different from the store [66](66-dead-letter-store-fails-the-scope-test.md) removes, which held things that *had* been delivered. If evidence later shows people need the payload back, that is a defensible thing to build — for the right reason this time.

## Acceptance Criteria

- [ ] A queued write can be given a maximum age, per route or globally.
- [ ] There is no expiry unless one is configured.
- [ ] Nothing is ever discarded silently.
- [ ] The setting is not `Ttl` and cannot be confused with it.

## Notes

- **Filed because removing something made a real question visible.** The misused concept was occupying the space its correct form would have wanted, so nobody asked whether the correct form was needed. Worth remembering as an argument for [ADR 0004](../docs/decisions/0004-default-to-removal.md): taking a thing out does not only reduce, it un-blocks.
- Sequence after [66](66-dead-letter-store-fails-the-scope-test.md). Building this while the word still means the other thing would be confusing to implement and worse to document.
