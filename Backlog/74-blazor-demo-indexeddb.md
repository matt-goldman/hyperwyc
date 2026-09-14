# Issue 74 — Blazor Demo, Using IndexedDB

## Summary

A Blazor WebAssembly sample alongside the MAUI one: the same API and the same offline read/write story, running in a browser with its data in IndexedDB.

The sample README already lists this as a planned target ("Blazor (WASM + Server): Planned, requires an IndexedDB store provider"), and `Hyperwyc.IndexedDb` sits in the unfiled roadmap items. Neither has a backlog file. This item captures the demo, and with it the store the demo cannot exist without.

## Status

💭 Under consideration, **v2.0+**, not on any critical path. Filed 2026-09-14.

## Why it is worth capturing

- **It is the one second store Hyperwyc has already said it would ship.** [ADR 0009](../docs/decisions/0009-provide-the-seam-not-the-alternatives.md) draws the line by asking whose gap a store fills. LiteDB fills a consumer preference, which the seam already serves. IndexedDB fills a platform Cabinet's file model cannot reach at all, so that store ships.
- **It is the first host that is not MAUI.** So far every claim about Blazor WASM (the trimming argument in [31](Done/31-package-structure.md), the missing change notifications in `NetworkAvailabilityConnectivityService`, the target-framework choices in [01](Done/01-repo-and-solution-setup.md)) comes from reasoning, not from running it.

## Questions a demo would force

**Which Blazor?** Only WebAssembly. With Blazor Server the `HttpClient` runs on the server, and a browser that goes offline loses its circuit, so Hyperwyc has nothing to intercept. The sample README's "WASM + Server" should probably lose the "Server". The WebAssembly half of Auto render mode is a separate, later question.

**Why not a Service Worker?** In a browser the real thing is available, and most apps should use it. This demo is a proof of concept, not a claim that it is better. The narrow case it serves: an app that wants offline reads and writes, does not need push notifications, and wants to keep all of its code in .NET rather than write a Service Worker in JS. That case may not really exist, which is why this sits at the end of the backlog. The demo's README should say so plainly, so it is not read as a recommendation.

**The store.** Does the demo build `Hyperwyc.IndexedDb`, or wait for it? If this item goes ahead, the store probably needs its own backlog item, because a shipped package carries obligations a sample does not. Known questions:

- Interop. IndexedDB is only reachable through JS, so is it a hand-rolled module or a library dependency? And what does that do to the AOT and trimming story ([53](Done/53-aot-json-serialization.md))?
- Encryption at rest. Cabinet encrypts, but .NET's cryptography support on `browser-wasm` is limited, so whatever this store promises has to be decided rather than inherited.
- Quota and eviction. The browser evicts under storage pressure, which reverses [42](42-cache-eviction.md)'s problem. `docs/decisions/README.md` already notes the inversion.
- Quarantine. `TryQuarantineAsync` means "move, don't delete", so where does an unreadable IndexedDB database go?
- It has to pass the conformance suite from [51](Done/51-cabinet-store-not-thread-safe.md), and the thread-safety requirement in `storage.md` still holds on a single-threaded runtime once async interleaving is involved.

**Connectivity.** `NetworkAvailabilityConnectivityService` gets no change notifications in a browser. A browser connectivity source means `navigator.onLine` and the `online`/`offline` events through JS interop. Whether that source is sample code or a shipped type is the same question [57](57-plugin-maui-hyperwyc.md) asks for MAUI, and [13](Done/13-connectivity-reference-implementation.md) answered it three times.

**Hosting.** The WebAssembly host is not the generic host. Check early whether `IHostedService` runs there at all, since that is [65](65-startup-flush-requires-a-host.md)'s problem on another platform.

**Blocking calls.** The browser runtime cannot block a thread on an incomplete task. `QueuedWrite.For` calls `ReadAsByteArrayAsync().GetAwaiter().GetResult()`. It is probably fine for buffered content, but it is the first thing to rule out.

**Transport failures.** An offline `fetch` surfaces through `HttpClient` differently from a socket failure. Confirm that [ADR 0007](../docs/decisions/0007-connectivity-cannot-cost-correctness.md)'s transport-failure path still classifies it correctly.

**Sample plumbing.** Add a Blazor WASM project to the existing Aspire AppHost, reuse `sample/Shared`, and configure CORS on the API service, which [18](Done/18-poc-web-api.md) noted a Blazor client would need.

## Acceptance Criteria

To be written if this moves out of consideration. At minimum: the demo says plainly that it is a proof of concept and not a recommendation over a Service Worker, and a decision on whether the IndexedDB store is filed as its own item before the demo is built.

## Related

- [ADR 0009](../docs/decisions/0009-provide-the-seam-not-the-alternatives.md): why this store ships when others do not.
- [71](71-store-benchmarks-and-alternative-implementations.md): alternative stores and benchmarks; an IndexedDB store belongs in the same comparison.
- [57](57-plugin-maui-hyperwyc.md): the same packaging question for a platform connectivity source.
- [42](42-cache-eviction.md): eviction, which a browser does for you.
- [65](65-startup-flush-requires-a-host.md): startup behaviour without a generic host.
