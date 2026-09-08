# Connectivity

Hyperwyc uses connectivity to decide which path to try first. It has a working fallback, so you do not have to supply one, but on a mobile device you should, and this page explains how.

**Building a .NET MAUI app?** [Hyperwyc in a .NET MAUI app](maui.md) has the implementation to copy and you can skip this page. 

**Wondering why this is a decision at all?** [Design](design.md#connectivity-is-an-optimisation-not-a-correctness-input) has the reasoning.

## The interface

```csharp
public interface IConnectivityService
{
    bool IsConnected { get; }
    IObservable<bool> ConnectivityChanged { get; }
}
```

`IsConnected` answers "right now". `ConnectivityChanged` is a **stream of changes, not a view of state** — nothing is replayed on subscribe — and it is what triggers a flush of queued writes.

## If you supply nothing

You get [`NetworkAvailabilityConnectivityService`](#networkavailabilityconnectivityservice), and one line in your logs at startup saying so. That is a deliberate default rather than a guess: it errs toward reporting connected, which is the recoverable direction, and it is the only shipped implementation that raises a change event, which is what makes queued writes go out without the application asking.

## Registering your own

```csharp
services.AddSingleton<IConnectivityService, MyConnectivityService>();
```

Order doesn't matter, before or after `AddHyperwyc()`, and it works the same if something else in your startup registers it on your behalf. Registering one suppresses the startup log line.

Alternatively, if you'd rather keep configuration in one place, or you already hold an instance, set it on the options instead:

```csharp
services.AddHyperwyc(options =>
{
    options.Connectivity = myConnectivityService;
});
```

A container registration takes precedence if you do both.

## Which implementation

|                                                                                         |                                                                                                                              |
| --------------------------------------------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------- |
| **[`NetworkAvailabilityConnectivityService`](#networkavailabilityconnectivityservice)** | Registered for you if you do nothing. No platform dependency. Good enough on a desktop or server; a starting point on mobile |
| **[`AlwaysOnlineConnectivityService`](#alwaysonlineconnectivityservice)**               | Reports connected, always. For a host that genuinely is, or when you want the response cache and nothing else                |
| **[.NET MAUI](maui.md)**                                                                | An implementation over `Connectivity.Current`, about twenty lines, to copy into your app                                     |
| **[Windows outside MAUI](#on-windows-the-answer-is-a-different-api-again)**             | `GetInternetConnectionProfile()`, which reports reachability rather than link state                                          |

**Write your own if reaching your API depends on something specific:** e.g. a VPN, a private APN, a particular interface or subnet. No general-purpose check can answer a question about a specific link, in either direction: a device with working internet and a dropped tunnel reports connected and still cannot reach your API.

### `NetworkAvailabilityConnectivityService`

Built on `NetworkInterface.GetIsNetworkAvailable()`, with no platform dependency.

> **It reports whether a network is available, not whether your API is reachable.** It catches
> the hard-offline cases — aeroplane mode, Wi-Fi off, cable unplugged — but reports connected
> behind a captive portal, on a router with no upstream, or on a mobile signal too weak to carry
> a request.

`IsConnected` is nothing but a call to `GetIsNetworkAvailable()`, which is more literal and lower
level than you might expect. What the BCL does inside that call is, in effect:

```csharp
// any interface, in System.Net.NetworkInformation
netInterface.OperationalStatus == OperationalStatus.Up
    && netInterface.NetworkInterfaceType != NetworkInterfaceType.Tunnel
    && netInterface.NetworkInterfaceType != NetworkInterfaceType.Loopback
```

An interface connection check, not a liveness probe.

> **`OperationalStatus` is [RFC 2863](https://datatracker.ietf.org/doc/html/rfc2863)
> `operStatus`** — *"able to pass packets"* — which is **link state, not administrative state**.
> An adapter that is merely enabled does not count: an unplugged ethernet port and a Wi-Fi adapter
> with no association both report `Down`, because neither has carrier. That is the part it gets
> right, and it is why aeroplane mode and an unplugged cable are caught.

What it cannot see is anything above layer 2. In practice that costs less than it sounds like,
because it errs in the safe direction: a false positive means the request goes out and the
transport fails, so a read is answered from the store and a write is queued exactly as if Hyperwyc
had known. You lose an attempt and some latency, nothing else.

### `AlwaysOnlineConnectivityService`

Reports connected, always.

It is not as destructive as it once was: a write attempted against a dead network fails at the
transport and is [queued from there](offline-writes.md#writes-are-queued-on-transport-failure-too-not-just-when-you-are-offline),
so writes are not lost. What you give up is everything that depends on *knowing* — every offline
read pays a full transport timeout before degrading, and **nothing is replayed automatically,
because no connectivity signal ever fires**. Under it, `FlushOnStartup` and an explicit
`FlushAsync()` are your only delivery triggers.

Choose it because your host really is always connected.

### On Windows, the answer is a different API again

Windows exposes `NetworkInformation.GetInternetConnectionProfile()`, whose
`GetNetworkConnectivityLevel()` returns `None`, `LocalAccess`, `ConstrainedInternetAccess` or
`InternetAccess`. On Windows this is better than anything above, because it reports reachability
rather than link state:

```csharp
var profile = NetworkInformation.GetInternetConnectionProfile();
var connected = profile?.GetNetworkConnectivityLevel() == NetworkConnectivityLevel.InternetAccess;
```

Hyperwyc ships no implementation over it: it is WinRT, so it needs a Windows target framework, and
including it would make Hyperwyc Windows-only. Microsoft's own documentation also warns that the
returned profile *"might or might not have internet access"*, so you check both the profile and
the level — and you still decide for yourself whether `ConstrainedInternetAccess` counts.

**Three platforms, three different right answers, none of them knowable from inside a library.**
That is the whole argument for shipping an implementation and declining to register it — see
[ADR 0006](decisions/0006-a-shipped-implementation-is-not-a-default.md).

## Which way an implementation errs

More important than how often it is wrong. Reporting online while offline is self-correcting,
because the transport is consulted and contradicts it; reporting offline while online is not,
because nothing is asked. Nothing is lost either way, but the second costs freshness and delays
writes — so **prefer erring toward connected**.

The full argument, including why probing your API or resolving its host name makes this worse
rather than better, is in [Design](design.md#connectivity-is-an-optimisation-not-a-correctness-input).

## Also

- **[Testing an app that uses Hyperwyc](testing.md)** — faking connectivity in your own tests.
- **[ADR 0007](decisions/0007-connectivity-cannot-cost-correctness.md)** — why connectivity is an
  optimisation rather than a correctness input, and the bound on that claim.
