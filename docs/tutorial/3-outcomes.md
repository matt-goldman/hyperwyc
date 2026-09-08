# 3. Finding out what happened

*[Tutorial index](README.md) · [← 2. A write that survives](2-writes.md) · Next: [4. Telling Hyperwyc about your network →](4-connectivity.md)*

You have a queued sale and a caller that has already moved on. When the write eventually goes out, something has to say so — and it has to say *which* write, because three sales queued to `/sales` are indistinguishable by URL and method.

## Subscribe to the event stream

```csharp
var hyperwyc = provider.GetRequiredService<IHyperwyc>();

hyperwyc.Events.Subscribe(new EventPrinter());

sealed class EventPrinter : IObserver<HyperwycEvent>
{
    public void OnNext(HyperwycEvent e) =>
        Console.WriteLine($"  {e.Type} {e.Method} {e.Url} " +
                          $"{e.CorrelationId} {e.Outcome?.Kind} {e.Outcome?.StatusCode}");

    public void OnError(Exception error) { }
    public void OnCompleted() { }
}
```

A plain `IObserver<T>`, because `IObservable<T>` is in the BCL and Hyperwyc takes no `System.Reactive` dependency. If your app already has Rx `hyperwyc.Events.Where(...)` works and reads better.

## Send it

The sale from the last page is still in the store. To send it, add one line to the end of your client — after the `POST`, before the `record` declarations:

```csharp
await hyperwyc.FlushAsync();
```

Comment out the `PostAsJsonAsync` line while you do, so the only thing this run does is deliver what is already queued. Then start the API and run the client again:

```
  OnDelivered POST http://localhost:5199/sales 0a499885-… Succeeded 201
```

The write went out, the server answered `201`, and the outcome came back on the stream with the same correlation id you got on the `202`. That id is the whole point: it is how the event you receive now joins up with the record you wrote then.

## Why you had to call it here

Hyperwyc has three delivery triggers: application start, connectivity restored, and `FlushAsync()`. In most applications the first two do the work and you rarely call the third, but neither of them fired here.

**The startup flush runs as an `IHostedService`, and your console app has no host.** You built a `ServiceCollection` and called `BuildServiceProvider()`, so nothing ever starts it. Under `Host.CreateApplicationBuilder()`, or in an ASP.NET Core app, the queued sale would have gone out on the next run without you asking for it.

**The connectivity trigger never fired either**, for a different reason: your machine's network was fine throughout. It was the API that was down, and a connectivity signal has nothing to say about that — which is the subject of the next page.

So calling `FlushAsync()` is necessary in this tutorial. In an application that has a host it is there for a user-facing "sync now" control, which you should offer if you show queued writes to your users.

There is no timer, no backoff and no schedule. Three triggers, and that is all, which is also why a write the server *answers* is finished with, whatever it said. See [Offline writes](../offline-writes.md#when-a-write-fails).

## What silence means

One thing the stream will not tell you: if a queued delivery attempt fails at the transport, **no event is published at all**. The outcome is recorded against the queued write and the flush stops. So a write that cannot be delivered goes quiet until it can be — nothing has gone wrong, and nothing is lost, but do not build a UI that waits for an event that is not coming.


## Use your own id

Hyperwyc generated a correlation ID and handed it back on the `202`, as `X-Hyperwyc-Correlation-Id`. To use it you would capture it at the call site and store it against your own record, so that when the event turns up later you can find the row it belongs to. That is a mapping table — small, but yours to keep in step.

You can skip it entirely. **If you set a correlation id yourself, Hyperwyc uses that instead:**

```csharp
var request = new HttpRequestMessage(HttpMethod.Post, "/sales")
{
    Content = JsonContent.Create(sale)
};
request.Options.Set(HyperwycRequestOptions.CorrelationId, sale.Id);

await http.SendAsync(request);
```

Now the event carries `sale.Id` — an id your own record already has — so there is nothing to map and nothing to keep in step. Hyperwyc does not require the value to be unique and does not deduplicate on it; it is your key, carrying your meaning.

## What you just proved

- A deferred write reports its outcome, correlated to something you chose.
- The outcome carries what the server actually said, not just success or failure.
- Delivery is automatic once something triggers it — a host starting, or the network returning. `FlushAsync()` is how you ask directly.

Everything so far has worked without you telling Hyperwyc a single thing about the network. That is deliberate, and convenient, but it has a cost you should know about before you ship.

*Next: [4. Telling Hyperwyc about your network →](4-connectivity.md)*
