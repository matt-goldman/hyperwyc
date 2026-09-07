# Pipeline placement

Where Hyperwyc's handler sits in your `HttpClient` pipeline, and what that means for authentication and for replayed requests.

Hyperwyc does not manage authentication, your existing handler does that. Register Hyperwyc's handler **first**, so everything after it also applies to replayed requests:

```csharp
services.AddHttpClient("MyApi")
    .AddHyperwycHandler()                     // queues and replays
    .AddHttpMessageHandler<AuthHandler>();    // adds a fresh token at send time
```

This works because **a replayed write goes back through the same pipeline it was made on.** Hyperwyc steps aside for replays, it doesn't re-queue them, and every handler after it runs normally. So a write queued on Monday and replayed on Tuesday is authenticated with Tuesday's token, not the one that was current when it was queued.

The same applies to anything else you put in the pipeline: logging, correlation IDs, telemetry, custom retry. Register it after `AddHyperwycHandler()` and replays get it too.

> **Use `AddHyperwycHandler()`, not `AddHttpMessageHandler<HyperwycHandler>()`.** The former
> captures the client's name, which is how Hyperwyc knows which pipeline to replay a queued
> write through. The plain form still works, but replays fall back to a bare transport with
> none of your handlers in it.

## What Hyperwyc sees, and what it leaves alone

Handler order follows the order you add them: **the first handler you add is the first to see the request, and the last to see the response.**

```
request  →  Hyperwyc  →  AuthHandler  →  network
response ←  Hyperwyc  ←  AuthHandler  ←  network
```

So a handler registered *after* `AddHyperwycHandler()` sees each response **before** Hyperwyc does, and can resolve failures Hyperwyc never learns about. If you already have a handler that catches a `401`, refreshes the token and retries, that is exactly what happens — Hyperwyc sees the successful retry, not the `401`.

That's the order you should deliberately adopt. **Hyperwyc is the last resort, not a retry layer**: it wraps the whole pipeline, so it only ever acts on failures your own handlers couldn't fix. It won't second-guess your auth, your circuit breaker or your fallbacks, and you don't need to configure it to stay out of their way.

Hyperwyc makes **one delivery attempt per queued write per flush** — it does not loop. A write the server rejects is failed immediately; one that fails transiently is left queued and tried again at the next opportunity. So your own retry handler, if you have one, composes rather than compounds: it retries within a single attempt, and Hyperwyc decides whether there should be another attempt at all.

## Replays and `ReplayTransport`

For the fallback case — a handler registered without a client name — `options.ReplayTransport` sets the transport replays use. It's also useful for exercising a flush in tests without network access, by supplying a stub. Hyperwyc never disposes it; one you provide stays yours to dispose.

> **Note — Hyperwyc short-circuits the pipeline when offline.** [Synthetic responses](responses.md) (`Queued`, `Offline`) are returned directly from the handler, so any `DelegatingHandler` placed *after* `HyperwycHandler` is **not** invoked on the offline path. This is by design — there is no outbound request to authenticate or otherwise mutate — but it means downstream handlers should not be relied upon for side effects that need to occur on every logical request (logging, telemetry, header stamping). For cross-cutting concerns that must run regardless of connectivity, place the handler **before** `HyperwycHandler` in the pipeline. For everything else, particularly handlers that could be costly or time-consuming to run if offline, place them after so that Hyperwwyc can intentionally short-circuit them.

