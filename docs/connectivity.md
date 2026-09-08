# Connectivity

Hyperwyc uses connectivity to decide which path to try first. It has a working fallback, so you do not have to supply one. But you should (especially on mobile), and this page is about why and how.

Hyperwyc is not the authority on connectivity, the transport is. **But only when it is asked.** The implementation of the connectivity service can be wrong, and which way an implementation errs decides whether the transport is asked at all.

This page explains the differences, what you should know, and when and how to provide your own connectivity service.

> 💡 **NOTE FOR .NET MAUI USERS**: You probably don't need to read this. Just copy [the sample code](#.net-maui-apps) into your app and register it in DI. You can read the rest of this if you are curious.
>    TODO: create a `Plugin.Maui.Hyperwyc` package that includes the `MauiConnectivityService`, takes a dependency on Hyperwyc,and wires everything up with a meta extension method on `MauiBuilder`.

[comment: Agreed, and filed. Worth flagging that it is an ADR-shaped question rather than a packaging one: ADR 0006 says a shipped implementation is not a default, and a meta extension method on MauiAppBuilder that wires one up *is* making the choice on the consumer's behalf. That is probably fine - on MAUI, Connectivity.Current is a choice Hyperwyc can make correctly, which is question 1 of the defaults test answered yes - but it is the first time Hyperwyc would register a connectivity source for someone, so it should be argued rather than assumed.]

[comment: The anchor below is broken. GitHub strips the leading dot when it builds the id, so it is #net-maui-apps, not #.net-maui-apps.]

## Summary

In some cases, a connectivity service could produce a false negative (report that your client is offline when it is not) or false positive (report offline when actually online). One of these can be an inconvenience, the other presents a potentially serious problem.

[comment: The two parentheticals say the same thing. A false positive is reporting online when actually offline. As written both read as "reports offline when online", which inverts the table immediately below and contradicts the passage further down the page where you have it right.]

The following table summarises these scenarios:


| Reported connectivity | Actual connectivity | Outcome                                                                                                                                                                                                                                                             |
| --------------------- | ------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Online                | Offline             | The request is attempted and the transport fails. A read is served from the store or reported as no data; a write is queued and replayed later. **Self-correcting**; it costs one failed request |
| Offline               | Online              | Hyperwyc responds to the request _before_ it reaches the transport. A read is served from the store, or reported as no data if the stored copy is past its TTL; a write is queued and answered `202`. **Not self-correcting**; it costs freshness, and delays the write |

Nothing is lost in either direction, but the second scenario hides a potential issue: the caller is told everything is fine, and the data it received is older than it needed to be. A write queued this way goes out on the next *connectivity change*, so an implementation that is briefly wrong costs a delay, and one that is **stuck** reporting offline never raises that change and never drains the outbox, while returning `202`s that look like success.

This is important to know when you write your own. It is not a hazard of the fallback: `GetIsNetworkAvailable()` reports false only when no ordinary interface is up at all, so its characteristic error is the first row, the harmless one. Erring toward "online" is the safe direction, which is also why [probing](#why-not-just-probe-the-api) doesn't work.

[comment: "reports false only when no ordinary interface is up at all" is right, and the VPN caveat 200 lines below is the exception that proves it rather than undermines it - a mesh client holds it at true, so it errs the same safe way. Worth one clause saying so here, because a reader who meets that caveat cold has to work that out for themselves.]

What a good implementation buys you, then, is the doomed request you did not make, fresher reads, and, most of all, a prompt signal when the network returns, because that is what sends queued writes.

[comment: This is the sharpest sentence on the page - the change signal, not the saved request, is the reason to write your own - and it is the one that does not make it into Getting started or the README.]

## Creating your connectivity service

The `IConnectivityService` interface is simple, it defines a method for getting the current connectivity status, and an `IObservable<bool>` letting you know when connectivity has changed.

```csharp
public interface IConnectivityService
{
    bool IsConnected { get; }
    IObservable<bool> ConnectivityChanged { get; }
}
```

Hyperwyc has two implementations in the package you can use for reference, and a sample implementation in a .NET MAUI app (which you can see in the sample code as well as below).

## If you supply nothing

You get [`NetworkAvailabilityConnectivityService`](#the-NetworkAvailabilityConnectivityService), and one line in your logs at startup saying so. That is a deliberate default, not a guess: it is the only shipped implementation that raises a change event, which is what makes queued writes go out without the
application asking.

[comment: Anchor case. GitHub lowercases heading ids and fragment matching is case-sensitive, so #the-NetworkAvailabilityConnectivityService will not resolve - it needs to be #the-networkavailabilityconnectivityservice.]

## Register it in your container

```csharp
services.AddSingleton<IConnectivityService, MyConnectivityService>();
```

Registering an implementation of `IConnectivityService` will silence the startup warning. Order doesn't matter, before or after `AddHyperwyc()`, whichever suits how your registrations are organised, and it works the same if something else in your startup registers it on your behalf.

[comment: There is no startup warning. ADR 0007 replaced the throw with a single LogInformation, and the code comment on it says explicitly "this is not a warning that anything is broken". The same stale reference appears near the end of the page as "not to get past the startup error".]

It's a runtime requirement, not a build time requirement. This is also important to be aware of.

[comment: Left over from when resolving without one threw. There is no requirement now - that is the whole of ADR 0007. Either cut it, or turn it into the point it is reaching for, which is worth making: a container registration is resolved late, so ordering does not matter. That is ADR 0003's second half ("requiring a decision must not require an ordering") and it is the reason the sentence above about order is true.]

[comment: "ff you'd rather" below.]

Alternatively, ff you'd rather keep the configuration in one place, or you already hold an instance, set it on the options instead:

```csharp
services.AddHyperwyc(options =>
{
    options.Connectivity = myConnectivityService;
});
```

A container registration takes precedence if you do both.

[comment: Checked this against the code and it holds in both orders - TryAddSingleton stands aside for an earlier container registration, and a later one wins because the last descriptor is the one resolved. Worth keeping; it is a claim readers will test.]

## Which implementation

Hyperwyc contains two implementations, and you can use these (one is wired up out of the box), but often supplying your own is a good idea.

* `AlwaysOnlineConnectivityService`: this provides no change detection so will never trigger a Hyperwyc queue flush, and it always reports online, so will attempt a network call even if offline. If you _always_ want to try the online path first and don't mind the added delay (not a good idea if you have an exponential backoff retry policy with Polly), and you are happy triggering the Hyperwyc write queue flush yourself, you can use this.
* `NetworkAvailabilityConnectivityService`: this is the version you get out of the box if you do nothing. It will work in most cases, but it has some limitations (discussed below) and there is likely a better implementation you can use.

[comment: Structural, and the main thing I would fix on this page. AlwaysOnlineConnectivityService is described in one line here and then again, at length and more accurately, near the very end of the page - inside the "Why not just probe the API?" section, which has nothing to do with it. The bullet above also omits the thing that changed most: a write attempted under it against a dead network is now queued from the transport failure rather than lost. Those two closing paragraphs belong here.]

### .NET MAUI Apps

If you're building a .NET MAUI app, you can copy and paste the implementation from the sample app (and expand the details below to see too) without any changes. It is not packaged because taking it as a dependency would put .NET MAUI in Hyperwyc's dependency graph for everyone, including console apps, services and Blazor hosts that have no use for it. The sample implementation is almost certainly never going to change, so owning it yourself isn't a risk, and it leaves you free to define "connected" as your app needs; treating `ConstrainedInternet` as offline, say, or folding in a health check against your own API.

The implementation below is the one in the [sample app](../sample/Hyperwyc.Sample.Maui/Services/MauiConnectivityService.cs), proven on an Android device: with Wi-Fi and mobile data disabled it reports disconnected, which is what routes a read to the cache.

[comment: Diffed this against the sample - they match exactly, which is worth knowing given how easy that is to let drift. But "The sample implementation is almost certainly never going to change" above sits awkwardly beside the Plugin.Maui.Hyperwyc TODO at the top, which would make it a maintained artefact with a version number. Both statements cannot stay as they are.]

<details>
<summary><b>`MauiConnectivityService`</b> - copy this and use in your .NET MAUI app</summary>

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

Five things in this that you should be aware of:

1. **No `System.Reactive`.** The first version used a `BehaviorSubject<bool>`, which meant a package reference existing for one field. Hand-rolling matches how Hyperwyc implements `IObservable<T>` internally and keeps a dependency out of your app that Hyperwyc deliberately avoids. If you already use Rx, then you might consider switching; and it's likely worthwhile in your mobile app anyway for several reasons.
2. **A change stream, not a state view.** Nothing is replayed on subscribe; `IsConnected` answers "right now". Getting this wrong is easy: an earlier version seeded a subject with `false` at startup, so a subscriber was told "offline" on connect while `IsConnected` read live state and said otherwise.
3. **Only publish on an actual change.** .NET MAUI raises `ConnectivityChanged` for any change in network access, including moving between Wi-Fi and cellular while staying online. Forwarding that as a connectivity restoration triggers a flush with nothing to send, so the service compares against the last value it published, seeded from live state, and only sends a new value if there's a change (see note below on constrained internet).
4. **`IDisposable`, to unhook the platform event.** `Connectivity.Current` is a long-lived static, so a handler left attached keeps the service and everything it captures alive for the process lifetime. Irrelevant for an app-lifetime singleton, but reference code gets copied into places where it isn't one. Note that `IConnectivityService` itself is not `IDisposable`; Hyperwyc never disposes your instance. Register it as a singleton and the container will.
5. **Events arrive on whatever thread the platform raised them on**, which on .NET MAUI is usually _not_ the UI thread. That's usually the correct approach, as you may not always want or need the UI to respond to the events. Also, forcing a dispatcher dependency into the service would make it untestable and useless off-platform. Respond to events on the UI thread as required (e.g. if you need to notify a user of a post-reconnection send success or failure).

`NetworkAccess.ConstrainedInternet` counts as disconnected here, the conservative reading, on the grounds that a captive portal is not the internet. If your API is reachable under it, flip that condition.

[comment: Those five notes are genuinely good, and they are also the clearest single example of the mixing you described. A reader told at the top of the page "just copy this" now gets five paragraphs on Rx, change streams versus state views, disposal of a static event, and platform threading before they reach anything else. That is deep-dive material. In a split this is the first block that moves.]

### On Windows, the best answer is a different API again

Windows exposes `NetworkInformation.GetInternetConnectionProfile()`, whose `GetNetworkConnectivityLevel()` returns `None`, `LocalAccess`, `ConstrainedInternetAccess` or `InternetAccess`. If your use case is on Windows, this is better than anything below, because it reports reachability
rather than link state.

```csharp
var profile = NetworkInformation.GetInternetConnectionProfile();
var connected = profile?.GetNetworkConnectivityLevel() == NetworkConnectivityLevel.InternetAccess;
```

There is no implementation in Hyperwyc that uses this, as it is WinRT, so needs a Windows target framework and including it would make Hyperwyc Windows only. Additionally, Microsoft's own documentation warns that the returned profile *"might or might not have internet access"*, so you have to check both the profile and the connectivity level, and still have to decide for yourself whether `ConstrainedInternetAccess` counts (same judgement call as `NetworkAccess.ConstrainedInternet` above).

[comment: This section is accurate and it is the strongest illustration on the page of why nothing is registered for you - three platforms, three different right answers, none of them knowable from inside Hyperwyc. It is currently framed as a Windows tip. It is doing the ADR 0006 work, and saying so would earn it the space it takes.]

### The NetworkAvailabilityConnectivityService

As you can see from the above descriptions of the .NET MAUI implementation, and the complications with the Windows connectivity APIs, choosing the right connectivity status signal depends on your platform and your back end. It's impossible for Hyperwyc to know, which is why nothing is registered for you; see [ADR 0006](decisions/0006-a-shipped-implementation-is-not-a-default.md).

**`NetworkAvailabilityConnectivityService`** ships in the box if you'd rather not build your own, built on `NetworkInterface.GetIsNetworkAvailable()` with no platform dependency:

```csharp
services.AddSingleton<IConnectivityService, NetworkAvailabilityConnectivityService>();
```

> **It reports whether a network is available, not whether your API is reachable.** It catches
> the hard-offline cases (aeroplane mode, Wi-Fi off, cable unplugged) but reports connected
> behind a captive portal, on a router with no upstream, or on a mobile signal too weak to
> carry a request.

To be clear, `GetIsNetworkAvailable` is literal, and lower level than you might expect. In the `NetworkAvailabilityConnectivityService`, the `IsConnected` getter is:

```csharp
// any interface, in System.Net.NetworkInformation
netInterface.OperationalStatus == OperationalStatus.Up
    && netInterface.NetworkInterfaceType != NetworkInterfaceType.Tunnel
    && netInterface.NetworkInterfaceType != NetworkInterfaceType.Loopback
```

This is a simple interface connection check, not a liveness probe or internet connectivity check.

[comment: Not quite what the code says. NetworkAvailabilityConnectivityService.IsConnected is literally `NetworkInterface.GetIsNetworkAvailable()` - the three-condition expression above is what the BCL does inside that call, not what the getter is. As presented, a reader could reasonably think Hyperwyc wrote that filter and could change it. "GetIsNetworkAvailable() is, in effect:" would fix it, and it is worth fixing because this is exactly the detail you said consumers should know.]

> **Note**: `OperationalStatus` is [RFC 2863](https://datatracker.ietf.org/doc/html/rfc2863) `operStatus` - *"able to pass packets"* — which is **link state, not administrative state**. So an adapter that is merely enabled does not count: an unplugged ethernet port and a Wi-Fi adapter with no association both report `Down`, because neither has carrier. That is the part it gets right, and it is why aeroplane mode and an unplugged cable are caught.

What it cannot see is anything above layer 2. It reports connected behind a captive portal, on a router with no upstream, and on a mobile signal too weak to carry a request.

> **A VPN or mesh interface can hold it at `true` on its own.** The tunnel exclusion is narrower than it looks: a TUN interface often reports `NetworkInterfaceType.Unknown` rather than `Tunnel`, so it is not excluded, and it does not necessarily go down when the physical link does. On a machine with every physical adapter down and a mesh client still running, this returns `true`. Different in kind from the cases above; that is not a degraded network path, it is not a path at all.

[comment: "that is not a path at all" is the sentence we corrected and it has come back in the redraft. In an enterprise or IoT deployment where the API sits inside the tunnel, that tunnel is the only path that matters and reporting connected is correct. The point is not that the check is wrong here - it is that whether it is wrong depends on where your API lives, which is the thing Hyperwyc cannot know. That makes this the best example on the page rather than a caveat. The same sentence is still in NetworkAvailabilityConnectivityService's XML docs, so it also ships in the package.]

`NetworkInterfaceType` is also not reliable across platforms: a Wi-Fi adapter reports as `Ethernet` on Linux, not `Wireless80211`, so filtering by type does not rescue this.

In practice that costs less than it sounds like, because it errs in the safe direction. A false positive means the request goes out and the transport fails: a read is then answered from the store exactly as if Hyperwyc had known it was offline, and a write is queued exactly as if it
had. You lose an attempt and some latency, nothing else. It's a reasonable choice for a desktop or server host, and a reasonable starting point on mobile until you write the platform version.

### Why not just probe the API?

The obvious next move is to make the check smarter — ping your API, or resolve its host name, and report *that*. Pinging is expensive and roughly duplicates the request your app makes anyway. Resolving looks cheaper, and while it technically is slightly, it can lead to worse assumptions.

**You cannot guarantee a fresh lookup.** `Dns.GetHostEntry` goes through the OS resolver, which caches — the DNS Client service on Windows, `systemd-resolved` on most Linux — and there is no "bypass the cache" flag. Being sure would mean speaking DNS yourself to a chosen server over
UDP/53: a dependency, and a port that is routinely blocked or intercepted.

**Negative caching makes it fail the wrong way.** `NXDOMAIN` and `SERVFAIL` are cached too, for the zone's SOA minimum. A lookup that failed while you were offline keeps failing after the network returns, so connectivity reports offline, the flush never fires, and queued writes sit
there. A false positive costs one wasted attempt; a false negative costs delivery. This trades the cheap failure for the expensive one.

**You cannot even test whether resolution is working.** A sensible seeming workaround is to attempt to resolve a dummy address, e.g. `{Guid.NewGuid()}.com`, and check the result for a difference between failed *resolution* and failed *request*. This unfortunately does not work, because `Dns.GetHostEntry` throws `SocketException` both when the name does not exist and when no resolver can be contacted. `SocketErrorCode` nominally separates `HostNotFound` from `TryAgain`, but which one you get is determined by the platform's resolver, so it is not something you can rely on.

> TODO: Can we verify whether platforms deterministically and consistently behave one way or the other? Because we might be able to use this if we can determine the runtime.

[comment: I would not pursue this, on two grounds.

First, it is not only the runtime. Which SocketError you get comes from the platform resolver and its configuration - systemd-resolved against plain glibc against a corporate resolver against whatever a mobile carrier interposes - so "determine the runtime" does not determine the answer, and the failure mode of getting it wrong is silent.

Second, and more decisive: even if it were perfectly deterministic it would not help, because the negative-caching problem in the paragraph above stands on its own. A lookup that failed while you were offline keeps failing after the network returns, and no amount of distinguishing TryAgain from HostNotFound changes that. The paragraph above already closes the question; this reopens it for a mechanism that would not fix the thing that made it a bad idea.]

**And a captive portal defeats it in the wrong direction.** A portal has to answer DNS in order to redirect you, so resolution *succeeds* behind one, but your API will still be unreachable.

Which leaves the conclusion the design already assumes: **the only reliable test of whether your API is reachable is a request to your API.** Hyperwyc makes that test on every flush. This is why "the transport is the authority".

As mentioned above, there are two potential failures of a connectivity check: a false negative, reporting not online when you are, or a false positive, reporting that you are online when you are not. A false positive is self correcting - your HTTP client makes the call and fails, which Hyperwyc helps with, and your app needs to handle anyway. The other case, a false negative, is where it becomes problematic, and this is fundamentally why Hyperwyc doesn't choose for you.

[comment: Third full statement of the asymmetry on this page (the Summary, then the paragraph about erring in the safe direction, then this) - and it also appears in responses.md, offline-writes.md, HyperwycOptions' XML docs and ADR 0007. It is the central idea, so some repetition is fair, but it should be written once at length and referenced from the rest. As it stands the three versions on this page have slightly different emphases and one of them, in the Summary, is wrong.]

**`AlwaysOnlineConnectivityService`** reports connected, always. Legitimate for a host that genuinely is, or when you want the response cache and nothing else.

It is not as destructive as it once was: a write attempted under it against a dead network now fails at the transport and is [queued from there](offline-writes.md), so writes are not lost. What you give up is everything that depends on *knowing* — every offline read pays a full
transport timeout before degrading, nothing is replayed automatically because no connectivity signal ever fires, and a `CacheFirst` route with a stale entry throws rather than serving. Choose it because your host really is always connected, not to get past the startup error.

[comment: These two paragraphs belong up under "Which implementation", beside the one-line bullet that currently describes this type. Nothing about AlwaysOnlineConnectivityService follows from the DNS discussion they are sitting in.]

## Faking connectivity in your own tests

Hyperwyc ships no test double, deliberately: `IConnectivityService` is two members, so a mocking library does it in a line and a hand-written fake does it in a few. Shipping one in the main package would mean a type you can accidentally reference from production code, and a separate
testing package is not worth publishing for this.

Most tests never need the change stream, because they drive sync explicitly with `IHyperwyc.FlushAsync()` rather than waiting for a connectivity event. That makes the fake almost nothing:

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

If you're testing the automatic flush on connectivity restoration rather than an explicit one, you need the stream to emit — reach for your mocking library's observable support, or a `Subject<bool>` from `System.Reactive` in the test project only. And if a test is simply online
throughout, `AlwaysOnlineConnectivityService` is already the fake you want.

[comment: Overall on this page: at 311 lines it is the longest in the docs and it is third in "Start here". For the audience you say is core the path should be quick start (copy this, done) -> this page (why, and what to do when the copy-paste is not right for you) -> ADR 0006/0007 (why there is a decision here at all). All the material exists; it is the ordering that does not. Roughly, the reference half is: the interface, what you get if you supply nothing, how to register, the two shipped implementations and what each costs. Everything else - the five notes, the Windows API, the DNS analysis, the testing section - is the second read.]

[comment: This section is for a third reader again - someone writing tests for their own application, on a different day from either of the other two. Good content, wrong page. It is the clearest single candidate for a short "Testing an app that uses Hyperwyc" page, which could also absorb the ReplayTransport-as-a-stub note from pipeline.md.]
