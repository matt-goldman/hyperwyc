# 4. Telling Hyperwyc about your network

*[Tutorial index](README.md) · [← 3. Finding out what happened](3-outcomes.md) · Next: [5. Varying it per route →](5-policies.md)*

Nothing you have written so far mentions connectivity, and everything worked. This page is about what that cost, and when it stops being acceptable.

## What has actually been happening

Hyperwyc asks an `IConnectivityService` which path to try first. You never registered one, so it used `NetworkAvailabilityConnectivityService`, which wraps the BCL's `NetworkInterface.GetIsNetworkAvailable()` and told it — correctly — that your machine has a working network. Your Wi-Fi was never the problem; the API was.

So every offline read and every queued write on the last three pages happened because **the transport failed**, not because Hyperwyc was told the device was offline. It attempted the request, the connection was refused, and it degraded on that.

That is the design, not a fallback from it: connectivity is an optimisation, and correctness rests on the transport. It is why the library works at all with no configuration.

## What it cost

Time. Every one of those reads paid a full connection attempt before falling back to the store, and on a desktop with a refused connection that is milliseconds. On a phone with a weak signal it is a timeout, and your user is looking at a spinner for all of it.

And one thing it cost that you would not have noticed here: **a queued write goes out when connectivity is restored**, and something has to raise that signal. On this desktop, the BCL does. On a mobile device it is the platform's connectivity API that knows, and a write can otherwise sit in the outbox until the next launch.

## Which way an implementation is wrong matters more than how often

There are two ways to be wrong, and they are not equally bad:

| Reported | Actual  | What happens                                                                                                                                            |
| -------- | ------- | ------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Online   | Offline | The request is attempted and the transport fails. A read is served from the store, a write is queued. **Self-correcting** — it costs one failed request |
| Offline  | Online  | Hyperwyc answers before the transport is reached. **Not self-correcting** — nothing contradicts it, because nothing was asked                           |

Nothing is lost either way. But the second costs freshness and delays a write, and an implementation *stuck* reporting offline never raises the change that would drain the outbox.

**Prefer erring toward connected.** The shipped default does, which is why it is safe to be the default. The reasoning is in [Connectivity](../connectivity.md) and [ADR 0007](../decisions/0007-connectivity-cannot-cost-correctness.md).

## Registering your own

One line, in either order relative to `AddHyperwyc`:

```csharp
services.AddSingleton<IConnectivityService, MyConnectivityService>();
```

The interface is two members:

```csharp
public interface IConnectivityService
{
    bool IsConnected { get; }
    IObservable<bool> ConnectivityChanged { get; }
}
```

`IsConnected` answers "right now". `ConnectivityChanged` is a stream of changes, not a view of state — nothing is replayed on subscribe, and it is what triggers a flush.

For .NET MAUI there is a complete implementation to copy in [Hyperwyc in a .NET MAUI app](../maui.md); on Windows outside .NET MAUI, [Connectivity](../connectivity.md#on-windows-the-answer-is-a-different-api-again) has the WinRT call that reports reachability rather than link state.

## Why Hyperwyc does not pick one for you

Because the right answer differs per platform and per application, and a wrong one fails quietly. Three hosts, three different correct implementations, none of them knowable from inside a library. It ships an implementation to point at and declines to register it — see [ADR 0006](../decisions/0006-a-shipped-implementation-is-not-a-default.md).

The one case worth naming: if reaching your API depends on something narrower than "a network" — a VPN, a private APN, a particular interface — then no general-purpose check answers your question in either direction, and you should write one that watches the thing you actually depend on.

## What you just proved

- The zero-config path works, and works because the transport is the authority.
- What you buy by supplying a connectivity service is latency and a prompt flush, not correctness.
- On a mobile device, that is worth buying.

One thing left: not every route wants the same treatment.

*Next: [5. Varying it per route →](5-policies.md)*
