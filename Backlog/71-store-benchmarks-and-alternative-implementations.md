# Issue 71 — Benchmark the Store, and Build the Alternatives Far Enough to Benchmark Them

## Summary

Hyperwyc has no numbers for its own storage layer. Build a benchmark harness that runs against `IHyperwycStore` rather than against any one implementation, then run it over `InMemoryStore`, `CabinetStore`, and LiteDB- and SQLite-backed implementations built to the point where they can be measured.

The output is **metrics and options**: evidence that Hyperwyc's performance — which is currently Cabinet's performance — is good enough, and a defensible answer to "why Cabinet and not the thing I already use". Most likely the alternatives are then kept as reference implementations rather than shipped as packages; see below.

## `IHyperwycStore` may move under this

**Not a dependency in either direction, and this item is not on anyone's critical path.** A caveat only: [66](66-dead-letter-store-fails-the-scope-test.md) would remove `MoveToDeadLetterAsync`, and [55](Done/55-envelope-kind-discriminator.md) may split `Envelope` in two and `UpsertAsync` with it.

That matters for the *alternative implementations* rather than the benchmarks — writing LiteDB and SQLite stores against a shape that is about to change means writing them twice. If this is picked up while either of those is live, do the harness and leave the implementations, or accept the rework knowingly.

The conformance-suite idea this is sometimes confused with is a different artefact and belongs to [51](Done/51-cabinet-store-not-thread-safe.md), where it is already recorded. That one is about verifying the contract; this one is about measuring performance.

## Status

💭 Under consideration, unscheduled. Filed 2026-09-09; **moved to v2.0+ on 2026-09-10** — it is worth doing and it is not on anyone's critical path, and the v1.2 listing implied otherwise. Inherits the benchmark criterion from [52](Done/52-store-rewrites-whole-set-per-write.md), which passed through [70](Done/70-move-bodies-to-cabinet-attachments.md) without being met.

## Why this is one item and not two

It reads like two — a benchmark harness, and a set of new store implementations — and it is deliberately kept as one, because **each is the other's justification**.

The alternatives cannot be planned separately, because whether they are worth finishing is exactly what the benchmark answers. Committing to ship `Hyperwyc.LiteDb` before measuring anything is committing to a package on the strength of a hunch; filing it as a follow-up means the follow-up gets planned at a moment when its own justification is not yet available.

The benchmark cannot be planned separately either, because a harness with one implementation in it measures nothing comparative. The comparison **is** the deliverable.

So the sequencing inside this item is the point: build each alternative to the minimum that passes the contract and can be measured, measure, and only then decide whether anything gets finished and shipped. **Build no further than the comparison requires until the numbers justify it.**

## The expectation, stated up front so the result can contradict it

The differences are expected to be negligible at the scale Hyperwyc operates at — a mobile cache and an outbox, not a dataset. Performance is cheap here, which is why this is not a blocker for anything.

That expectation is worth writing down because it changes what a useful result looks like. If everything lands within noise, the finding is not "no difference"; it is **"storage choice is not a performance decision for this workload, so choose on AOT, encryption and dependencies instead"** — which is a genuinely useful thing to be able to say with evidence, and is currently a claim nobody can support.

## What to measure

Against `IHyperwycStore`, so every implementation is measured through the same surface an application sees:

| Measurement                                                           | Why it is on the list                                                                                                                         |
| --------------------------------------------------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------- |
| **Single-record write cost as a function of store size**              | The criterion inherited from [52](Done/52-store-rewrites-whole-set-per-write.md), and the one with a specific prediction attached — see below |
| Cache read (`GetCachedResponseAsync`) against a populated store       | The hot path; runs on every request that might be served offline                                                                              |
| Outbox read (`GetPendingOutboxAsync`), which loads, filters and sorts | Runs per flush, and hydrates every body it returns                                                                                            |
| Prefix invalidation over N entries                                    | Still N full-set rewrites in Cabinet ([68](Done/68-cache-invalidation-leaves-tombstones.md)); the worst case by a distance                    |
| Cold start — first read after construction                            | Where an index or a schema is paid for, and where a mobile app feels it                                                                       |
| Bytes on disk for a given logical cache                               | Feeds Android's 25 MB backup quota ([48](Done/48-exclude-store-from-os-backup.md)) and [42](42-cache-eviction.md)                             |

**The specific prediction to confirm or kill:** a store holding 100 cached 500 KB responses should have gone from rewriting tens of megabytes per cache write to under a megabyte, after [70](Done/70-move-bodies-to-cabinet-attachments.md). The mechanism is in place and asserted by a test; the number has never been measured.

**Cabinet publishes its own benchmarks against SQLite and LiteDB, and they do not answer this.** They measure Cabinet's API on Cabinet's workload. Reading them as though they described Hyperwyc's access pattern is the exact mistake [ADR 0008](../docs/decisions/0008-shrink-what-you-store.md) was written about — a bulk-write table being taken for a statement about incremental updates to a growing set.

## The second reason to build alternatives, which is not performance

**It is the only real test of whether `IHyperwycStore` is reimplementable.**

[Issue 51](Done/51-cabinet-store-not-thread-safe.md) happened because every store test ran against `InMemoryStore`, which serialised everything, while `CabinetStore` serialised nothing and the interface required neither. [storage.md](../docs/storage.md#implementing-your-own) now tells implementers to test against both shipped stores for exactly that reason. A third and fourth implementation, written by someone reading that documentation rather than the code, is the strongest available check that the contract is stated well enough to build against — and every place it is not is a documentation defect found before a consumer finds it.

That value is independent of the numbers, and it survives a decision not to ship any of them.

## The likely outcome: reference implementations, not packages

Stated as a prior rather than a conclusion, because the numbers are allowed to move it.

If the benchmark shows Cabinet at parity or better, the alternatives have done their job the moment they are measured. They will have proved the default is as good as anything considered, and proved the interface is buildable against — and neither of those requires a published package. **The expected end state is that all four implementations stay in the repo, two of them ship, and the other two exist as reference code and contract subjects.**

That is not a new pattern here. [Issue 13](Done/13-connectivity-reference-implementation.md) reached the same shape for the MAUI connectivity implementation and put it plainly: *reference code carries nearly the same value as a shipped type at none of the structural cost.* This is that decision applied to storage, and the general form is [ADR 0009](../docs/decisions/0009-provide-the-seam-not-the-alternatives.md).

## The constraints on the alternatives are Hyperwyc's reasons, not a consumer's

Worth separating carefully, because it is easy to write these up as though they settled the question for everyone. They do not.

**For Hyperwyc's default they are close to decisive.** LiteDB uses dynamic expressions and is not AOT-safe, which is the exact failure [53](Done/53-aot-json-serialization.md) was closed to prevent in a library whose primary audience ships iOS release builds with AOT on by default. SQLite brings native dependencies and its encryption story is SQLCipher, with commercial implications, or nothing — and Hyperwyc persists request headers verbatim, `Authorization` included ([30](30-sensitive-header-exclusion.md)), so unencrypted at rest is a regression against the default rather than a peer of it. These are also, almost line for line, the reasons Cabinet was written in the first place.

**For a consumer they may be irrelevant.** Plenty of consumers are not on MAUI at all. Plenty do not need encryption at rest, because nothing they cache warrants it or the platform provides it a layer down. Plenty already run LiteDB or SQLite and would rather have one store in their application than two. For any of them the trade goes the other way, and they are not wrong — they are answering a different question with different inputs.

So the constraints justify **Cabinet as the default**. They do not justify refusing anyone else the seam, and they are not a reason to talk a consumer out of a choice that is right for them. That is the whole point of `Hyperwyc.Core` shipping independently and the store being configurable: a strong opinion, expressed as a default, with the door left open. Providing the alternatives ourselves is a third thing, and not implied by either.

**These are also assumptions with a shelf life.** LiteDB could fix its AOT story; .NET's SQLite encryption options are not frozen. A founding premise that is never re-tested quietly stops being true, and this is the exercise that re-tests it.

## Acceptance Criteria

- [ ] A benchmark project measuring the table above through `IHyperwycStore`, runnable on demand and not in CI by default.
- [ ] **Single-record write cost does not grow with store size** — measured, with the 100 × 500 KB prediction from [70](Done/70-move-bodies-to-cabinet-attachments.md) either confirmed or corrected in writing.
- [ ] LiteDB- and SQLite-backed `IHyperwycStore` implementations, each passing the same contract tests as the shipped stores, and each built no further than that.
- [ ] Results recorded somewhere durable — a document in the repo, not a terminal scrollback — including the shape of the workload, so a future reader can tell what was and was not measured.
- [ ] **A written decision on each alternative: ship, keep as reference code, or delete.** With the reasoning. A negligible difference is an argument for choosing on other grounds, not an argument for shipping everything — and "the interface held, the numbers held, the code stays as a reference" is the expected answer rather than a disappointing one.
- [ ] Whatever is kept without being shipped is **findable and honest about its status** — a consumer looking for a LiteDB store should reach it and be told plainly that it is reference code, not a supported package. See [13](Done/13-connectivity-reference-implementation.md) for how that was handled for connectivity.
- [ ] Any contract ambiguity the new implementations expose is fixed in [storage.md](../docs/storage.md) or the interface's own docs, not worked around in the implementation.

## Notes

- **Not a blocker for anything.** Performance is cheap at this scale, and this is filed to replace a suspicion with a measurement, not to fix a problem anyone has hit.
- **The question is whether Cabinet is good enough, not which store wins.** "Good enough" is subjective and the benchmark cannot make it objective; what it can do is show that Hyperwyc's performance — which is currently Cabinet's performance — is at least the equal of the alternatives a sceptical reader would name. That converts a design choice from an assertion into something with evidence behind it.
- Suggested milestone **v1.2**, alongside the other operational-visibility work. Author's call.
- The harness is worth more than any single run of it: [42](42-cache-eviction.md) and [67](67-configurable-response-retention.md) both change what the store costs, and neither currently has a way to show it.
- Benchmarks belong out of the default test run. A store benchmark that takes minutes and varies with the machine is not a thing to gate a pull request on.

## Related

- [Issue 52](Done/52-store-rewrites-whole-set-per-write.md) — where the write-cost criterion came from.
- [Issue 70](Done/70-move-bodies-to-cabinet-attachments.md) — the change whose effect is still unmeasured.
- [Issue 51](Done/51-cabinet-store-not-thread-safe.md) — why more implementations is a correctness argument and not only a performance one.
- [Issue 31](Done/31-package-structure.md) — the package split that anticipated exactly these providers, and made adding one possible without a binary-breaking change.
- [Issue 13](Done/13-connectivity-reference-implementation.md) — the same conclusion reached for connectivity: reference code, not a shipped type.
- [ADR 0008](../docs/decisions/0008-shrink-what-you-store.md) — on not mistaking somebody else's benchmark for your own workload.
