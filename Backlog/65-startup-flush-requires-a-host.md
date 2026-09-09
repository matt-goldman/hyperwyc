# Issue 65 — The Startup Flush Silently Does Nothing Without a Host

## Summary

`FlushOnStartup` is documented as one of Hyperwyc's three delivery triggers and defaults to `true`. It is implemented as an `IHostedService`, so it only runs somewhere that starts hosted services. In an application built on a bare `ServiceCollection` it never fires, and nothing says so.

## Status

⬜ Open. Filed 2026-09-08, found while reviewing the tutorial — the tutorial's own client is a bare `ServiceCollection`, so the page was about to tell readers that delivery is automatic in an app where it is not.

> **[66](66-dead-letter-store-fails-the-scope-test.md) changed the default to `false`**, for its own reason: the event became the only report of a delivery, so a flush inside host startup would race a subscriber that attaches later and lose the outcome in silence. That takes the sting out of this item — a trigger nobody is relying on cannot silently fail to fire — but it does not close it. **Option 1 is still the answer and still unwritten**, and the MAUI question is untouched: whether `MauiApp` starts hosted services at all is a fact about the platform, not about the default.

## Measured

Same store directory across all three, one write queued with the API down, then the API brought up:

```
run 1 (api down, no host):                              POST -> 202
run 2 (api up, plain ServiceCollection, no FlushAsync):  delivered = False
run 3 (api up, real Host, no FlushAsync):                delivered = True
```

Nothing is lost — the write stays queued and goes out on the next connectivity change or explicit `FlushAsync()`. What is lost is a trigger the consumer believes they have.

## Who this affects

- **Console apps and worker-style clients** built with `new ServiceCollection()`. Straightforwardly documentation.
- **Test harnesses**, which routinely build a bare provider. Arguably fine, since tests should drive `FlushAsync()` explicitly — [testing.md](../docs/testing.md) already says so.
- **.NET MAUI — unverified, and the one that matters.** `MauiApp` builds a service provider without an `IHost` lifetime, and whether it starts `IHostedService` implementations needs checking on a device rather than reasoning about. **If it does not, `FlushOnStartup` has never fired in a MAUI app**, and every queued write has been going out on the connectivity-change trigger alone. The sample never calls `FlushAsync()`, so it would not have surfaced: `MauiConnectivityService` raises a change on reconnect and the outbox drains from there, which looks identical from the outside.

That last one is the whole reason this is filed rather than fixed in a sentence. It is also a reason for [57](57-plugin-maui-hyperwyc.md) to exist: a MAUI-specific package is the natural place to start the flush explicitly, whatever the answer turns out to be.

## Options

1. **Document it.** `offline-writes.md`'s trigger table gains a condition, and `maui.md` says what to do. Cheapest, and necessary regardless of what else happens.
2. **Have `AddHyperwyc` fall back** to flushing from somewhere that does not need a host. Rejected on sight — it would mean doing I/O from a service constructor or from the first request, both of which are the kind of magic [ADR 0004](../docs/decisions/0004-default-to-removal.md) exists to keep out.
3. **Say so at startup**, the way the missing connectivity service does: if `FlushOnStartup` is `true` and nothing started the hosted service, nobody ever finds out. There is no clean hook for that — the absence is exactly what makes it undetectable.

1 is the answer. 3 is worth a moment's thought only because the same wrong-and-silent property that made connectivity worth a log line applies here too.

## Acceptance Criteria

- [ ] The trigger is documented as conditional on a host, wherever the three triggers are listed.
- [ ] The MAUI question has an answer, tested on a device.
- [ ] If MAUI does not run hosted services, `docs/maui.md` tells a reader what to do instead, and [57](57-plugin-maui-hyperwyc.md) picks it up.

## Notes

- **Found by testing a claim rather than reading it.** The tutorial said "in a running application you usually would not call `FlushAsync` — Hyperwyc flushes on startup", which is true of a hosted app and false of the console app the tutorial had just walked the reader through building. Matt spotted the tension while reviewing and asked whether the flush would not simply happen by itself; the honest answer needed a harness rather than an opinion.
- The tutorial now explains *why* `FlushAsync()` is needed there, which is better material than the claim it replaced: neither automatic trigger fires, and the reasons differ — no host for the first, and a perfectly healthy network for the second.
