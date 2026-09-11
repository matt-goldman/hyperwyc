# Issue 67 — Configurable Retention of Delivered Responses

## Summary

[66](66-dead-letter-store-fails-the-scope-test.md) removes retention entirely: once a write is delivered, Hyperwyc keeps nothing, and the [event](../docs/events.md) is the only report of what the server said.

That is the right default and it has one real cost. This item is the deliberate, later answer to it — opt-in retention of *responses*, with a retention policy the consumer chooses.

**Filed now precisely so it does not evaporate.** Deferring it is correct; forgetting why it was deferred is not.

## Status

💭 Under consideration, unscheduled. Filed 2026-09-09. **Depends on [66](66-dead-letter-store-fails-the-scope-test.md)**, and should not start until that has shipped and had some time to prove the absence is survivable.

## The evidence it exists for

A rejection often carries the only explanation of itself, and that explanation cannot be reconstructed later.

The criterion is not a status range. It is: **did the server tell you something you cannot get any other way?**

|                     | Reconstructable?                                                                                                                                                                 |
| ------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `400`, `409`, `422` | **No.** A validation payload, a `ProblemDetails`, an account of what conflicted — this is the whole reason the response existed, and re-reading the resource does not produce it |
| `401`, `403`, `404` | Usually yes. The code carries the meaning; a re-read gives the same answer                                                                                                       |
| `429`               | **Not from the body** — the actionable part is `Retry-After`, a header                                                                                                           |

Any response saying *the problem is at the caller's end* should tell the caller what to do about it, and one that does not is bad API design. `400` and `409` are the two where a well-designed API almost always includes a body, and `400` more often than `409` — `ProblemDetails` exists for exactly this, and ASP.NET Core returns one for model-binding failures without being asked.

### This reopens the headers question

`DeliveryOutcome.Headers` was removed in the [ADR 0004](../docs/decisions/0004-default-to-removal.md) audit, correctly, because nothing used it. But if the criterion is "information you cannot reconstruct", `429`'s `Retry-After` qualifies and is not in the body. So does `WWW-Authenticate` on a `401`, for a consumer that cares.

Retention and `DeliveryOutcome.Headers` are therefore the same decision, and this item cannot be designed without settling it.

### And it couples to the request being discarded

[66](66-dead-letter-store-fails-the-scope-test.md) also discards the request once delivered — correct, and not only for size: the request body and its headers are the most sensitive things in the store ([30](Done/30-sensitive-header-exclusion.md)), and keeping them after delivery extends that exposure for no benefit.

The consequence is that a retained `400` says *"field X is invalid"* about a request Hyperwyc no longer holds. That is fine, because the application has its own record and its own correlation id, and joining them is the [application-owned-store pattern](50-resilient-applications-guide.md) it was going to build anyway. But it is a real coupling and should be stated rather than discovered.

## Shape

Sketched, not specified. **Ship it as one release** — half of this is worse than none of it, because a retention feature without eviction is a leak.

- **Opt in, empty by default.** No retention unless asked for. The default must not encode Hyperwyc having an opinion about what a status code *means* — that was the whole of 66.
- **Status filter**, expressed as specific includes, or a global include with specific excludes. The consumer supplies the judgement; Hyperwyc applies it. That is [ADR 0003](../docs/decisions/0003-default-what-you-can-decide-correctly.md)'s distinction between a default we choose and a value you supply.
- **Per route, through `RoutePolicy`** if at all — not a second route-matching mechanism. A retained/not-retained member overriding the global default.
- **Eviction**, by store size, record age, or record count. **Excluding anything unsent**, always: eviction is for history, an unsent write is work, and dropping work is the failure this library exists to prevent.
- **Responses only.** The request is gone by then, deliberately.

## Acceptance Criteria

- [ ] Retention is off unless configured, and configuring it requires the consumer to say which responses.
- [ ] Nothing unsent is ever evicted, under any setting.
- [ ] The headers question is answered — either `DeliveryOutcome.Headers` returns with a reason, or the item records why the body alone is enough.
- [ ] Store growth is bounded under every configuration, including the most permissive one.

## Notes

- **Do not build this before it is asked for.** The point of removing retention in 66 is to find out whether anyone needs it back. If nobody does, the correct outcome is that this item is closed unbuilt, like [26](Done/26-v2-typed-response-shaping.md) — which is a success, not a failure.
- The reason it is filed anyway: "consider it in future" has a way of becoming permanent absence by accident rather than by decision. If it is never built, that should be because nobody needed it, not because nobody remembered the `400` case.
