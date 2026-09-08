# 2. A write that survives

*[Tutorial index](README.md) · [← 1. A read that works offline](1-reads.md) · Next: [3. Finding out what happened →](3-outcomes.md)*

A read that cannot be served costs the user a refresh. A write that cannot be sent costs them their work. This is the half Hyperwyc exists for.

## Add a write endpoint

In the API:

```csharp
app.MapPost("/sales", (Sale sale) => Results.Created($"/sales/{sale.Id}", sale));

record Sale(string Id, int ProductId, int Quantity);
```

Start the API again and leave it running.

## Post a sale

In the client:

```csharp
var sale = new Sale(Guid.NewGuid().ToString(), ProductId: 1, Quantity: 3);
var response = await http.PostAsJsonAsync("/sales", sale);

Console.WriteLine($"{(int)response.StatusCode} {response.ReasonPhrase}");

record Sale(string Id, int ProductId, int Quantity);
```

```
201 Created
```

The real response from the real server, returned unchanged. While it is online, Hyperwyc is not in the way at all.

## Stop the API and post again

`Ctrl+C` the API, then:

```csharp
var response = await http.PostAsJsonAsync("/sales", sale);

Console.WriteLine($"{(int)response.StatusCode} " +
    $"{response.Headers.GetValues("X-Hyperwyc-Status").First()} " +
    $"{response.Headers.GetValues("X-Hyperwyc-Correlation-Id").First()}");
```

```
202 Queued 0a499885-0b6c-4f2e-9b0e-1f6c1f0a7f31
```

**`202 Accepted`** — the request has been accepted for later processing and has not yet been performed against the origin server, which is exactly what has happened. It is also how you tell a queued write from a delivered one without reading a header, as long as no ordinary success from your own API is a `202`.

The write is now in a durable store on disk. Kill the process, restart the machine, come back tomorrow: it is still there.

## What Hyperwyc did *not* do

It did not decide the network was down and skip the attempt. Look at what actually happened: the default connectivity service reported **connected** — your Wi-Fi is fine, it is the API that is gone — so Hyperwyc tried the request, the transport refused the connection, and *that* is what told it the write could not be sent.

This matters more than it sounds. The connectivity service is a hint about which path to try first. The transport is what actually knows, and where it is consulted it gets the last word. A wrong hint costs you one failed attempt, not the write. See [ADR 0007](../decisions/0007-connectivity-cannot-cost-correctness.md).

## The route that must not be queued

Some writes are worse to defer than to fail. A payment, a seat reservation, anything where many actors are changing one value — sending it two hours late may be worse than not sending it.

```csharp
services.AddHyperwyc(options =>
{
    options.Routes.For("/payments/*", RoutePolicy.NetworkOnly());
});
```

A write to a `NetworkOnly` route is never queued. It goes to the transport and fails as it would if Hyperwyc were not installed, which is the honest answer when deferring is the wrong one. More on this in [page 5](5-policies.md).

## What you just proved

- A write made with nothing listening is kept, durably, and answered `202`.
- Your calling code did not branch on connectivity to make that happen.
- Hyperwyc worked it out from the transport failing, not from being told the device was offline.

The write is safe. What it is not, yet, is *sent* — and your user has been told "submitted", which is a promise you now have to keep. That is next.

*Next: [3. Finding out what happened →](3-outcomes.md)*
