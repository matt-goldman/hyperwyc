# Architecture Decision Records

Decisions that shaped Hyperwyc, and the reasoning behind them — kept so that a future decision of
the same shape can be answered consistently, rather than re-argued from scratch or quietly
reversed.

| # | Decision | Status |
|---|---|---|
| [0001](0001-idempotency-is-not-hyperwycs-remit.md) | Idempotency is not Hyperwyc's remit | Accepted |

## What belongs here

An ADR when a decision **constrains future decisions**: something about Hyperwyc's scope, its
responsibilities, or the boundary between it and the applications using it. Especially a decision
to *not* build something, or to remove something already built, since those are the ones most
easily undone by someone who only sees the gap and not the reason for it.

Not everything needs one. A bug fix does not. Nor does a choice with an obvious right answer and
no lasting implications.

## How this relates to the other documents

| Document | Question it answers |
|---|---|
| [README](../../README.md) | How do I use it? |
| [TECHNICAL_PLAN](../../TECHNICAL_PLAN.md) | What does it do today, and how is it built? |
| [ROADMAP](../../ROADMAP.md) | What is coming? |
| [Backlog](../../Backlog/README.md) | What is the work, and what state is each piece in? |
| **decisions** | **Why is it this way, and what does that imply for what comes next?** |

A backlog item records what was done and how. An ADR records *why*, and what the decision means
for the next one — which is why the two are separate even when they cover the same change. The
backlog entry for a decision goes to `Done/` and stops being read; the ADR is meant to be read
again.

## Conventions

- Numbered sequentially, four digits, never reused.
- Status is `Proposed`, `Accepted`, `Superseded by NNNN`, or `Deprecated`. A superseded ADR stays
  where it is with its status updated; the reasoning that turned out to be wrong is often the
  most useful part.
- Link to the backlog item that implemented it, so the *what* is one hop away.

## The standing scope test

[ADR 0001](0001-idempotency-is-not-hyperwycs-remit.md) derives a test for whether a capability
belongs in Hyperwyc at all. Reproduced here because it is meant to be used, not filed:

1. Would this problem exist without Hyperwyc? If yes, it has existing owners.
2. Does it require anything of the consumer's API? If yes, it is an imposition.
3. Can the application already do it at the call site or in its own handler? If yes, our job is
   to not interfere.
4. Does it depend on something only Hyperwyc knows — connectivity, that a request is queued, that
   a request is a replay, or the contents of the outbox? If no, it belongs elsewhere.
