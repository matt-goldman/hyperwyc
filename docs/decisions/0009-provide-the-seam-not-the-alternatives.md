# 9. Provide the seam, not the alternatives

**Status:** Proposed

**Date:** 2026-09-09

## Context

Hyperwyc holds a strong opinion about storage and builds a real seam around it. `AddHyperwyc()` gives you Cabinet with no configuration; `Hyperwyc.Core` ships independently so you can supply your own `IHyperwycStore` and never reference Cabinet at all. Both halves are deliberate, and [issue 31](../../Backlog/Done/31-package-structure.md) sized the package split specifically so a second store could be added later without a binary-breaking re-split.

[Issue 71](../../Backlog/71-store-benchmarks-and-alternative-implementations.md) proposes building LiteDB- and SQLite-backed stores in order to benchmark against them and to prove the interface is buildable against. That raises the question the seam has always implied and never had to answer: **when Hyperwyc builds an implementation through its own seam, does it ship it?**

Three acts are separable, and it is easy to treat them as one:

1. Build the seam, so a consumer can make a different choice.
2. Ship a default through it, so most consumers never have to.
3. Ship the alternatives, so a consumer who differs finds their choice already made for them.

Hyperwyc does 1 and 2. The question is whether 2 obliges it to do 3, and whether having *written* an alternative for other reasons changes the answer.

## Decision

**Hyperwyc ships the storage implementation it recommends, and keeps any alternative it builds as reference code in the repository.**

The seam is how Hyperwyc supports a different choice. A published package is not part of that support, and building an implementation for benchmarking or contract-validation does not convert it into one.

Generally: **enabling a choice and making it are different acts, and the first does not oblige the second.** Get out of the way; do not walk the path on their behalf.

## Rationale

**The opinion is real and belongs in the default.** Cabinet is AOT-safe, encrypted at rest with no configuration and no unencrypted mode, and carries no native dependencies. Those are the right properties for a library whose primary audience ships iOS release builds with AOT on by default, and they are why [53](../../Backlog/Done/53-aot-json-serialization.md) was worth closing. Defaulting to it is Hyperwyc making a decision it can make correctly, per [ADR 0003](0003-default-what-you-can-decide-correctly.md).

**Those properties are Hyperwyc's requirements, not the consumer's.** Many consumers are not on MAUI. Many do not need encryption at rest, because nothing they cache warrants it or the platform provides it a layer down. Many already run SQLite or LiteDB and would rather have one store in their application than two. For them the trade goes the other way and they are not mistaken — they are answering a different question with different inputs. The seam exists precisely so that answer is available to them.

**But a shipped package is a standing promise, not a convenience.** It is versioning, support, security response, and keeping pace with a dependency's own churn, indefinitely. Taking that on for a store Hyperwyc would not recommend spends a real and finite budget on a position it does not hold — and the consumer who wanted it is, by construction, someone who already knows the library they chose.

**Reference code carries nearly the same value at none of the structural cost.** That is [issue 13](../../Backlog/Done/13-connectivity-reference-implementation.md)'s finding, reached independently for the MAUI connectivity implementation and applied here to storage. A consumer who wants a LiteDB store wants a worked example against a contract, and an example they copy is one they own — including the decisions in it that only they can make.

**It keeps the recommendation legible.** A published `Hyperwyc.LiteDb` reads as endorsement, and would have to ship alongside a warning that it is not AOT-safe. "Here is our package, do not use it on the platform this library is mainly for" is not a coherent thing to say. Reference code says what it is without the contradiction.

**The distinction is one Hyperwyc has drawn before.** [ADR 0006](0006-a-shipped-implementation-is-not-a-default.md) separated *shipping* an implementation from *registering* it — its decision has since been superseded, but the distinction it drew has not, and this is the same cut one step further out: shipping the seam is not shipping what goes through it.

## What this is not

**Not a rule against ever shipping a second store.** The test is whose gap it fills. An IndexedDb store for Blazor WASM would exist because Cabinet's file model does not reach that platform at all — that is Hyperwyc failing to serve a host, not a consumer preferring something else, and it ships. A LiteDB store exists because someone prefers LiteDB, which the seam already serves.

**Not a claim the alternatives are bad.** They are good tools, chosen well by many people, and the reasons Cabinet is Hyperwyc's default are reasons about Hyperwyc's audience rather than defects in anything else.

**Not licence to leave the seam untested.** The opposite: if the seam is the whole of the support, it has to be demonstrably buildable against, which is exactly why [71](../../Backlog/71-store-benchmarks-and-alternative-implementations.md) builds the alternatives even though it does not expect to ship them.

**Not a decision to hide the code.** Unshipped is not unfindable. A consumer looking for a LiteDB store should reach the reference implementation and be told plainly what it is.

## Consequences

**The alternatives built under [71](../../Backlog/71-store-benchmarks-and-alternative-implementations.md) are expected to stay in the repository unshipped**, serving as benchmark subjects, contract tests, and copyable examples. That is the successful outcome of that item, not a failure to finish it.

**The contract documentation carries more weight than it would otherwise.** If the seam is the support, then [storage.md](../storage.md#implementing-your-own) and `IHyperwycStore`'s own docs are the product for anyone who differs. Ambiguity found there is a defect, which is why 71 requires it fixed in the docs rather than worked around in an implementation.

**Someone will see the gap and try to fill it.** A repository containing a working LiteDB store that is not published looks like an oversight to anyone who does not know why. That is the specific reason this is written down rather than left as a preference.

## The test this establishes

When Hyperwyc has built an implementation through one of its own seams and is deciding whether to publish it:

1. **Does its absence leave a host or platform unserved?** If Hyperwyc cannot work there at all without it, ship it. If the seam already serves that case, do not.
2. **Would Hyperwyc recommend it?** A package that has to ship with a warning against its main audience is a contradiction, not a service.
3. **Is the value in the code or in the package?** If a consumer would copy it and change it anyway, reference code delivers the value and leaves them owning the result.

## Related

- [ADR 0006](0006-a-shipped-implementation-is-not-a-default.md) — shipping versus registering; the same cut, one step in.
- [ADR 0004](0004-default-to-removal.md) — the instinct this applies to packages.
- [Issue 13](../../Backlog/Done/13-connectivity-reference-implementation.md) — the same conclusion, reached first for connectivity.
- [Issue 31](../../Backlog/Done/31-package-structure.md) — the package split that makes a second store possible, which is what made this question worth answering.
- [Issue 71](../../Backlog/71-store-benchmarks-and-alternative-implementations.md) — the item this decides in advance.
