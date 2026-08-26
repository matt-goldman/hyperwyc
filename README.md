# Hyperwyc

> **A service-worker-inspired HTTP handler for .NET** — your app code never needs to know whether it's online or offline.

Hyperwyc sits in the `HttpClient` pipeline and transparently handles caching, queuing, and replay. Like a [Service Worker](https://developer.mozilla.org/en-US/docs/Web/API/Service_Worker_API) in a PWA, it intercepts outgoing HTTP requests and returns normal-looking responses regardless of connectivity. Your existing `HttpClient` code doesn't change. The `X-Hyperwyc-Status` header is always present on synthetic responses for code that *wants* to know.

Hyperwyc is backend-agnostic, storage-pluggable, and designed for scenarios where data conflicts are rare or handled server-side.

---

## Features

- ✅ **Service-worker-inspired** — transparent 200 OK responses by default; callers never branch on connectivity
- ✅ Backend-agnostic HTTP caching and replay layer (REST/JSON over HTTP/1.x; v1.0)
- ✅ Offline request queue, replayed when connectivity returns
- ✅ Response cache with expiry policies
- ✅ Write-triggered GET cache invalidation
- ✅ Configurable offline response policy (transparent 200 or explicit 503)
- ✅ Pluggable connectivity and cache policy
- ✅ Observables for sync lifecycle events
- ✅ Works with any `HttpClient`, minimal blast radius
- ✅ Sends the request your app made — no headers added, nothing required of your API

---

## Quick Start

```bash
dotnet add package Hyperwyc
```

```csharp
services.AddSingleton<IConnectivityService, MyConnectivityService>();

services.AddHttpClient("MyApi")
    .AddHyperwycHandler()
    .AddHttpMessageHandler<AuthHandler>();

services.AddHyperwyc();
```

That's the whole setup. Storage needs no decision — `AddHyperwyc()` gives you a durable,
encrypted store out of the box.

The one thing Hyperwyc can't decide for you is how to tell whether the device is online, so
you supply an `IConnectivityService`. Register it like any other service and you're done; if
you don't have an implementation, [Connectivity](#connectivity) covers your options, one of
which ships in the box.

Everything else has a working default, and is there when you want it:

```csharp
services.AddHyperwyc(options =>
{
    options.DefaultPolicy = SyncPolicy.CacheFirst(TimeSpan.FromDays(1));
});
```

### Packages

| Package | Use it when |
|---|---|
| `Hyperwyc` | Almost always. Includes durable [Cabinet](https://github.com/mattgoldman/cabinet)-backed storage and works with no configuration |
| `Hyperwyc.Core` | You are supplying your own `ISyncStore`. No storage dependency; call `AddHyperwycCore<TStore>()` instead |

The store is a type parameter on `AddHyperwycCore<TStore>()` rather than a setting, so forgetting it is a compile error rather than a silent fall back to in-memory storage that loses everything on restart:

```csharp
services.AddHyperwycCore<MyCustomStore>();          // container constructs it
services.AddHyperwycCore(sp => new MyStore(...));   // or supply a factory
```

### Storage location and encryption

By default the store lives in a `Hyperwyc` folder under `LocalApplicationData` — inside the app sandbox on Android and iOS — and is encrypted with AES-256-GCM using a key derived from that path.

That default costs you nothing and keeps cached data from casual inspection of the device filesystem, but the derived key is deterministic, so it is not a defence against an attacker who has the device and knows what this library does. If the cached data warrants more, supply your own key:

```csharp
services.AddHyperwyc(configureStore: store =>
{
    store.DirectoryPath = myPath;
    store.EncryptionKey = keyFromSecureStorage;   // 32 bytes
});
```

Losing that key means losing access to everything already stored.

---

## When to Use

- You want a **service-worker-like** drop-in resilience layer for .NET HTTP clients
- You need offline resilience without rewriting your app around a sync framework
- You want API calls to look and feel the same online or offline
- You want transport-level durability, not a storage-first sync engine
- Your app already has a stable API contract and you don't want to rearchitect

## What It Doesn't Do

- Doesn't handle auth or token refresh (your own handler should — place it after `HyperwycHandler`)
- Doesn't dictate your data model
- Doesn't replace your local database
- Doesn't resolve data conflicts — it's designed for scenarios where conflicts are rare or handled server-side

---

## How It Works

Hyperwyc sits in your `HttpClient` pipeline as a `DelegatingHandler` — the same interception point that a Service Worker occupies for browser `fetch()`. It transparently handles all outgoing requests:

- **Online:** Requests are sent immediately. Responses are optionally cached according to your staleness policy.
- **Offline writes:** Requests are serialised and queued locally. The caller receives a `202 Accepted` (by default) with an `X-Hyperwyc-Status: Queued` header. When connectivity is restored, the queue is replayed in order. `202` is used rather than `200` because the request has been accepted for later processing but not yet performed against the origin server. The eventual outcome arrives on [`SyncEvents`](#sync-events), correlated back to the write that produced it.
- **Offline reads:** Served from cache if available (even if stale — any data is better than no data offline). If no cache exists, the caller receives a `200 OK` with `X-Hyperwyc-Status: Offline` and a body of `null`.
- **Online reads (GET/HEAD/OPTIONS):** Served from cache if fresh; fetched from the API if stale or missing.

### Caching strategies

The default is cache-first. Set `options.DefaultPolicy` to choose a different one:

| Policy | Behaviour |
|---|---|
| `SyncPolicy.CacheFirst()` | Serve a fresh cached response; otherwise fetch. Uses `DefaultCacheTtl` |
| `SyncPolicy.CacheFirst(ttl)` | As above, with the freshness window stated on the policy |
| `SyncPolicy.ApiFirst()` | Always fetch; fall back to the cache only if the request fails |
| `SyncPolicy.CacheOnly()` | Serve from cache regardless of age; never touch the network |
| `SyncPolicy.NetworkOnly()` | Always fetch; never read or write the cache |

Offline, `CacheFirst` and `ApiFirst` both serve stale cached data rather than nothing, and
`CacheOnly` behaves the same as it does online. `NetworkOnly` opts out of the cache entirely,
so it has nothing to offer offline.

A `CacheOnly` read that finds nothing cached returns `X-Hyperwyc-Status: CacheMiss` rather than
`Offline` — the device may be online, and the request was withheld by policy, not connectivity.

The app doesn't need to know the difference. Your existing code doesn't change.

> **Why "no data" instead of "no connection"?** Connectivity is an infrastructure concern, not an application one. Your code already has to handle the empty-result path (a search with no matches, a feed with no items); offline simply produces the same shape. A caller that does want to know reads the `X-Hyperwyc-Status` header — or, for a write, the `202`, which no ordinary success is.

### Designing your responses

When Hyperwyc has nothing to give you — an offline read with no cached copy, or a write it has
only queued — it returns the JSON `null` literal, not an empty body. That distinction matters
more than it looks:

```csharp
var product  = await http.GetFromJsonAsync<Product>("/products/1");        // null
var products = await http.GetFromJsonAsync<List<Product>>("/products");    // null
```

Both return `null` rather than throwing. An *empty* body would throw `JsonException` from inside
the extension method — for a single object every bit as much as for a collection — because an
empty body is not "no data", it is not JSON at all.

So the case you need to handle is the one you already handle: a `null` result.

```csharp
var products = await http.GetFromJsonAsync<List<Product>>("/products");

if (products is null)
{
    // Offline with nothing cached. Show an empty state, or check
    // X-Hyperwyc-Status if you want to say why.
    return [];
}
```

**A collection comes back as `null`, not empty.** Returning `[]` would need Hyperwyc to know the
route returns a collection, which is per-route knowledge it does not have — planned as part of
per-route policies. Until then, a null-coalesce at the call site covers it.

If you would rather branch on status codes than on `null`, set
the `X-Hyperwyc-Status` header to find out. And if your API
already uses an envelope or result type — [`Ardalis.Result`](https://github.com/ardalis/Result)
or a hand-rolled `ApiResponse<T>` — that keeps working, since the envelope simply deserialises
to `null` and your existing handling takes over.

### Current limitations

- **Buffered bodies only.** Request and response bodies are read into memory in full before being queued or cached. Binary payloads round-trip byte for byte — file uploads, image downloads, protobuf, gzip — but streaming uploads and downloads of indeterminate length are not supported.

---

## Auth Handler Placement

Hyperwyc does not manage authentication — your own handler does. Register Hyperwyc's handler
**first**, so everything after it also applies to replayed requests:

```csharp
services.AddHttpClient("MyApi")
    .AddHyperwycHandler()                     // queues and replays
    .AddHttpMessageHandler<AuthHandler>();    // adds a fresh token at send time
```

This works because **a replayed write goes back through the same pipeline it was made on.**
Hyperwyc steps aside for replays — it doesn't re-queue them — but every handler after it runs
normally. So a write queued on Monday and replayed on Tuesday is authenticated with Tuesday's
token, not the one that was current when it was queued.

The same applies to anything else you put in the pipeline: logging, correlation IDs, telemetry,
custom retry. Register it after `AddHyperwycHandler()` and replays get it too.

> **Use `AddHyperwycHandler()`, not `AddHttpMessageHandler<HyperwycHandler>()`.** The former
> captures the client's name, which is how Hyperwyc knows which pipeline to replay a queued
> write through. The plain form still works, but replays fall back to a bare transport with
> none of your handlers in it.

### What Hyperwyc sees, and what it leaves alone

Handler order follows the order you add them: **the first handler you add is the first to see
the request, and the last to see the response.**

```
request  →  Hyperwyc  →  AuthHandler  →  network
response ←  Hyperwyc  ←  AuthHandler  ←  network
```

So a handler registered *after* `AddHyperwycHandler()` sees each response **before** Hyperwyc
does, and can resolve failures Hyperwyc never learns about. If you already have a handler that
catches a `401`, refreshes the token and retries, that is exactly what happens — Hyperwyc sees
the successful retry, not the `401`.

That's deliberate. **Hyperwyc is the last resort, not a retry layer**: it wraps the
whole pipeline, so it only ever acts on failures your own handlers couldn't fix. It won't
second-guess your auth, your circuit breaker or your fallbacks, and you don't need to configure
it to stay out of their way.

Hyperwyc makes **one delivery attempt per queued write per flush** — it does not loop. A write
the server rejects is failed immediately; one that fails transiently is left queued and tried
again at the next opportunity. So your own retry handler, if you have one, composes rather than
compounds: it retries within a single attempt, and Hyperwyc decides whether there should be
another attempt at all.

### Replays and `ReplayTransport`

For the fallback case — a handler registered without a client name — `options.ReplayTransport`
sets the transport replays use. It's also useful for exercising a flush in tests without network
access, by supplying a stub. Hyperwyc never disposes it; one you provide stays yours to dispose.

> **Note — Hyperwyc short-circuits the pipeline when offline.** Synthetic responses (`Queued`, `Offline`) are returned directly from the handler, so any `DelegatingHandler` placed *after* `HyperwycHandler` is **not** invoked on the offline path. This is by design — there is no outbound request to authenticate or otherwise mutate — but it means downstream handlers should not be relied upon for side effects that need to occur on every logical request (logging, telemetry, header stamping). For cross-cutting concerns that must run regardless of connectivity, place the handler **before** `HyperwycHandler` in the pipeline.

---

## Sync Events

Subscribe to `IObservable<SyncEvent>` to observe requests moving through the sync lifecycle:

```csharp
hyperwyc.SyncEvents.Subscribe(e => Console.WriteLine($"{e.Type}: {e.Url}"));
```

| Event | Meaning |
|-------|---------|
| Event | Meaning | Carries an outcome |
|-------|---------|---|
| `OnQueued` | Request persisted to local queue (offline) | No — nothing has been attempted |
| `OnSynced` | Request successfully delivered | Yes |
| `OnFailed` | Request dead-lettered — the server refused it | Yes |
| `OnUpdated` | Cached response refreshed | No |

These are Hyperwyc's own events, not your app's lifecycle — see below for how the two relate.

### Knowing which request an event is about

When a write is deferred, the caller has already been given a `202` and moved on. For the
eventual outcome to be useful, the event has to say *which* write it concerns — three sales
queued to `/sales` are indistinguishable by URL and method.

Every event carries a `CorrelationId`. **If you set one, Hyperwyc uses it**, which means you can
correlate on an id you already have and keep no mapping table:

```csharp
var request = new HttpRequestMessage(HttpMethod.Post, "/sales")
{
    Content = JsonContent.Create(sale)
};
request.Options.Set(HyperwycRequestOptions.CorrelationId, sale.Id.ToString());

await client.SendAsync(request);
```

If you don't, Hyperwyc generates one and returns it on the `202` as
`X-Hyperwyc-Correlation-Id`, so you can record the association at the moment you queue:

```csharp
var response = await client.PostAsJsonAsync("/sales", sale);
sale.CorrelationId = response.Headers.GetValues("X-Hyperwyc-Correlation-Id").Single();
```

The header is present either way. Hyperwyc neither requires the value to be unique nor
deduplicates on it — it is your key, carrying your meaning. It is **not** an idempotency key;
see [ADR 0001](docs/decisions/0001-idempotency-is-not-hyperwycs-remit.md).

Events also carry `RequestId` (Hyperwyc's own unique envelope id, which a diagnostics view would
use) and `RequestBody`, so you can deserialise your own payload back out if you'd rather not
keep a copy.

### Reading the outcome

`SyncOutcome` is what the server — or the network — actually said:

```csharp
hyperwyc.SyncEvents
    .Where(e => e.Type == SyncEventType.OnFailed)
    .Subscribe(e =>
    {
        var outcome = e.Outcome!;

        if (outcome.Kind == SyncOutcomeKind.Rejected)
        {
            // The server refused it. outcome.StatusCode is 409, and the reason is in the body.
            var detail = outcome.GetBodyAsText();
            ShowRejection(e.CorrelationId!, outcome.StatusCode, detail);
        }
        else
        {
            // Never reached the server. Still queued; nothing to do but wait.
            ShowPending(e.CorrelationId!);
        }
    });
```

| Member | What it tells you |
|---|---|
| `Kind` | `Succeeded`, `Rejected` (a 4xx — it will never work), `TransientFailure` (a 5xx/408/429), `TransportFailure` (never reached the server) |
| `StatusCode`, `ReasonPhrase`, `Headers` | As returned, or `null`/empty for a transport failure |
| `Body`, `GetBodyAsText()`, `BodyTruncated` | The response body, up to `MaxOutcomeBodyBytes` (16 KB by default), clipped rather than dropped if longer |
| `Error` | The transport failure message. A string rather than an exception, because this record is persisted |

`OnSynced` carries an outcome too. A replayed `POST` may answer with the created resource —
server-assigned ids, normalised values — which the caller never saw, so this is how you reconcile
your local record with what was actually stored.

> **Two things this does not do.** Failure detail is persisted on the envelope so a dead-lettered
> write can still explain itself after a restart, but **success detail is not** — a delivered
> envelope leaves the outbox, so if your app was killed mid-flush that response is gone. Re-read
> the resource if you need certainty. And Hyperwyc has no opinion on what you do with any of
> this: prompt, auto-reduce, back-order, escalate, discard. It hands you what the server said and
> stops there.

<details>
<summary>A framing note, if it's useful</summary>

Surfacing outcomes this way nudges you toward describing the action rather than its result —
"order **submitted**", not "order **successful**" — with the outcome arriving separately and
later. Most teams already think this way about their backend without having carried it into the
client. It is genuinely optional: Hyperwyc does not require anyone to model their UI a particular
way.

</details>

---

## Connectivity

Hyperwyc needs to know whether the device can reach the network, and it has **no default for
this, on purpose**.

That is a deliberate exception to how the rest of the library behaves. Hyperwyc can pick a
store for you because any durable store will do. It cannot pick a connectivity source, because
the right answer depends on the platform — and a wrong one fails quietly. If it assumed
"always online", every request would take the network path, nothing would ever be queued, and
nothing would ever be replayed. You'd have a caching library that looked like it was working.

### Register it in your container

```csharp
services.AddSingleton<IConnectivityService, MyConnectivityService>();
```

That's it. Order doesn't matter — before or after `AddHyperwyc()`, whichever suits how your
registrations are organised, and it works the same if something else in your startup registers
it on your behalf.

If you'd rather keep the configuration in one place, or you already hold an instance, set it on
the options instead:

```csharp
services.AddHyperwyc(options =>
{
    options.Connectivity = myConnectivityService;
});
```

A container registration wins if you do both. Do neither and Hyperwyc throws the first time
anything needs it, with a message listing these options.

### Which implementation

**Write one against your platform.** `IConnectivityService` is two members, so this is short —
and it's what most apps should use.

```csharp
public interface IConnectivityService
{
    bool IsConnected { get; }
    IObservable<bool> ConnectivityChanged { get; }
}
```

It's a copy rather than a package because taking it as a dependency would put MAUI in
Hyperwyc's dependency graph for everyone, including the console apps, services and Blazor hosts
that have no use for it. Owning it costs you very little, and it leaves you free to define
"connected" as your app needs — treating `ConstrainedInternet` as offline, say, or folding in a
health check against your own API.

The implementation below is the one in the [sample app](sample/Hyperwyc.Sample.Maui/Services/MauiConnectivityService.cs),
proven on an Android device: with Wi-Fi and mobile data disabled it reports disconnected, which
is what routes a read to the cache.

<details>
<summary><b>MauiConnectivityService</b> — copy this</summary>

```csharp
using Hyperwyc.Interfaces;

public sealed class MauiConnectivityService : IConnectivityService, IDisposable
{
    private readonly object _gate = new();
    private readonly List<IObserver<bool>> _observers = [];

    private bool _lastPublished;
    private bool _disposed;

    public bool IsConnected => Connectivity.Current.NetworkAccess == NetworkAccess.Internet;

    public IObservable<bool> ConnectivityChanged { get; }

    public MauiConnectivityService()
    {
        _lastPublished = IsConnected;
        ConnectivityChanged = new ChangeStream(this);

        Connectivity.Current.ConnectivityChanged += OnPlatformConnectivityChanged;
    }

    public void Dispose()
    {
        IObserver<bool>[] observers;

        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;

            observers = [.. _observers];
            _observers.Clear();
        }

        Connectivity.Current.ConnectivityChanged -= OnPlatformConnectivityChanged;

        foreach (var observer in observers)
            observer.OnCompleted();
    }

    private void OnPlatformConnectivityChanged(object? sender, ConnectivityChangedEventArgs e)
    {
        var connected = e.NetworkAccess == NetworkAccess.Internet;

        IObserver<bool>[] observers;

        lock (_gate)
        {
            if (_disposed || connected == _lastPublished) return;

            _lastPublished = connected;
            observers = [.. _observers];
        }

        foreach (var observer in observers)
            observer.OnNext(connected);
    }

    private void Subscribe(IObserver<bool> observer)
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                _observers.Add(observer);
                return;
            }
        }

        observer.OnCompleted();
    }

    private void Unsubscribe(IObserver<bool> observer)
    {
        lock (_gate)
            _observers.Remove(observer);
    }

    private sealed class ChangeStream(MauiConnectivityService owner) : IObservable<bool>
    {
        public IDisposable Subscribe(IObserver<bool> observer)
        {
            ArgumentNullException.ThrowIfNull(observer);

            owner.Subscribe(observer);
            return new Subscription(owner, observer);
        }

        private sealed class Subscription(MauiConnectivityService owner, IObserver<bool> observer)
            : IDisposable
        {
            private IObserver<bool>? _observer = observer;

            public void Dispose()
            {
                var subscribed = Interlocked.Exchange(ref _observer, null);
                if (subscribed is not null)
                    owner.Unsubscribe(subscribed);
            }
        }
    }
}
```

</details>

Five things in there are deliberate, and worth understanding before you change them:

**No `System.Reactive`.** The first version used a `BehaviorSubject<bool>`, which meant a
package reference existing for one field. Hand-rolling matches how Hyperwyc implements
`IObservable<T>` internally and keeps a dependency out of your app that Hyperwyc deliberately
avoids. If you already use Rx, by all means use a subject — the interface doesn't care.

**A change stream, not a state view.** Nothing is replayed on subscribe; `IsConnected` answers
"right now". Getting this wrong is easy: an earlier version seeded a subject with `false` at
startup, so a subscriber was told "offline" on connect while `IsConnected` read live state and
said otherwise.

**Only publish on an actual change.** MAUI raises `ConnectivityChanged` for any change in
network access, including moving between Wi-Fi and cellular while staying online. Forwarding
that as a connectivity restoration triggers a flush with nothing to send, so the service
compares against the last value it published — seeded from live state — and stays quiet
otherwise.

**`IDisposable`, to unhook the platform event.** `Connectivity.Current` is a long-lived static,
so a handler left attached keeps the service and everything it captures alive for the process
lifetime. Irrelevant for an app-lifetime singleton, but reference code gets copied into places
where it isn't one. Note that `IConnectivityService` itself is not `IDisposable` — Hyperwyc
never disposes your instance. Register it as a singleton and the container will.

**Events arrive on whatever thread the platform raised them on**, which on MAUI is usually not
the UI thread. That's intentional: forcing a dispatcher dependency into the service would make
it untestable and useless off-platform. Marshal in your subscriber if you're touching UI.

`NetworkAccess.ConstrainedInternet` counts as disconnected here — the conservative reading, on
the grounds that a captive portal is not the internet. If your API is reachable under it, flip
that condition.

**`NetworkAvailabilityConnectivityService`** ships in the box if you'd rather not, built on
`NetworkInterface.GetIsNetworkAvailable()` with no platform dependency:

```csharp
services.AddSingleton<IConnectivityService, NetworkAvailabilityConnectivityService>();
```

> **It reports whether a network is available, not whether your API is reachable.** It catches
> the hard-offline cases — aeroplane mode, Wi-Fi off, cable unplugged — but reports connected
> behind a captive portal, on a router with no upstream, or on a mobile signal too weak to
> carry a request.

In practice that costs less than it sounds like. Connectivity is a hint about which path to
take; a false positive means the request goes out and fails at the transport, which Hyperwyc
already handles by abandoning the flush and waiting for the next signal. You lose a wasted
attempt and some latency, not correctness. It's a reasonable choice for a desktop or server
host, and a reasonable starting point on mobile until you write the platform version.

**`AlwaysOnlineConnectivityService`** reports connected, always. Legitimate for a host that
genuinely is — or when you want the response cache and nothing else. **Nothing is ever queued
or replayed under it**, so don't reach for it just to get past the startup error.

### Faking connectivity in your own tests

Hyperwyc ships no test double, deliberately: `IConnectivityService` is two members, so a mocking
library does it in a line and a hand-written fake does it in a few. Shipping one in the main
package would mean a type you can accidentally reference from production code, and a separate
testing package is not worth publishing for this.

Most tests never need the change stream, because they drive sync explicitly with
`IHyperwyc.FlushAsync()` rather than waiting for a connectivity event. That makes the fake
almost nothing:

```csharp
internal sealed class FakeConnectivity(bool connected = true) : IConnectivityService
{
    public bool IsConnected { get; set; } = connected;

    public IObservable<bool> ConnectivityChanged { get; } = new Never();

    private sealed class Never : IObservable<bool>
    {
        public IDisposable Subscribe(IObserver<bool> observer) => new Noop();
        private sealed class Noop : IDisposable { public void Dispose() { } }
    }
}
```

```csharp
var connectivity = new FakeConnectivity(connected: false);

services.AddSingleton<IConnectivityService>(connectivity);
// ... queue a write while offline ...

connectivity.IsConnected = true;
await hyperwyc.FlushAsync();
```

If you're testing the automatic flush on connectivity restoration rather than an explicit one,
you need the stream to emit — reach for your mocking library's observable support, or a
`Subject<bool>` from `System.Reactive` in the test project only. And if a test is simply online
throughout, `AlwaysOnlineConnectivityService` is already the fake you want.

## When Hyperwyc Syncs

Queued writes are flushed on exactly two triggers:

| Trigger | When |
|---|---|
| Application start | `FlushOnStartup` (default `true`), if the device is online |
| Connectivity restored | While the app is running, debounced by 2 seconds |

Plus an explicit call, for a user-facing "sync now" control:

```csharp
await hyperwyc.FlushAsync();   // IHyperwyc, resolved from DI
```

### You don't need to hook app lifecycle events

**Shutting down or backgrounding the app is deliberately not a sync trigger**, and you should
not add one. Writes are only ever queued because connectivity was poor — and closing the app
doesn't improve connectivity, so a flush at that moment would fail for the same reason the work
was queued in the first place.

Anything still queued is replayed at next launch. Nothing is lost, so there is nothing to
rescue on the way out.

This matters most on mobile, where it wouldn't work anyway: Android and iOS terminate suspended
processes without running disposal, finalizers, or any cleanup you might have registered.
Durability comes from the outbox being persistent, not from tidying up at exit.

### When a write fails

Not every failure means the same thing, so Hyperwyc doesn't treat them the same way:

| Failure | What happens |
|---|---|
| `4xx` — the server rejected it | Dead-lettered immediately. Sending the identical request again cannot change the answer |
| `5xx`, `408`, `429` — the server is struggling | Left queued, and attempted again on the next flush. There is no attempt budget: Hyperwyc keeps the write until the server takes it or you discard it |
| Can't reach the network at all | The flush stops and nothing is held against the queued writes — the network being down says nothing about them |

Hyperwyc's job is getting writes out once the network allows it, so that is the failure it
retries. Anything your own handlers already deal with — refreshing a token, tripping a circuit
breaker, retrying a flaky endpoint — has run before Hyperwyc sees the result, and it doesn't
second-guess them.

### Duplicate writes

Any retry can deliver the same request twice — if a response is lost after the server has
already committed, the retry looks identical to a first attempt. This is true of a Polly retry
handler, a user double-tapping a button, or a proxy replaying a request. Hyperwyc's retry carries
the same risk and no more.

**Hyperwyc takes no position on it.** It adds no headers and asks nothing of your API; duplicate
suppression is between your application and your backend. If it matters to you, approaches people
use include:

- **Client-generated domain identity** — the record carries an id chosen by the client, so a
  repeated write updates rather than duplicates. Idempotent by construction, and nothing in the
  transport needs to know.
- **The [`Idempotency-Key`](https://datatracker.ietf.org/doc/draft-ietf-httpapi-idempotency-key-header/)
  header**, set at the call site, if your backend implements it. Hyperwyc persists request headers
  and replays them unchanged, so a key you set once stays stable across every retry:

  ```csharp
  request.Headers.Add("Idempotency-Key", sale.Id.ToString());
  ```

- **A correlation or transaction id you already emit** — common in event-driven systems, and
  increasingly generated in the UI so analytics can be tied to backend telemetry.

These are things people do, not a recommendation from Hyperwyc. Which one fits, or whether the
concern applies at all, depends on your API.

### Interrupted syncs

If a flush is cut short — the app is backgrounded mid-replay, or the process is killed — the
envelopes it hadn't delivered stay queued and go out on the next trigger. They are not marked
as failed, and they are not dead-lettered. Only a request the server actually rejected, after
the server refuses, ends up in the dead-letter queue.

---

## Further Reading

- [TECHNICAL_PLAN.md](TECHNICAL_PLAN.md) — How Hyperwyc works today: architecture, components, storage model, and design rationale
- [ROADMAP.md](ROADMAP.md) — Where it's going: milestones and planned features
- [Backlog/README.md](Backlog/README.md) — Per-item status, priority, and dependencies
- [docs/decisions](docs/decisions/README.md) — Architecture decision records: why Hyperwyc is the way it is, and what it deliberately does not do
- [POC.md](POC.md) — Sample application and proof-of-concept setup
