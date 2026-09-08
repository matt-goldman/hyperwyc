# 1. A read that works offline

*[Tutorial index](README.md) · Next: [2. A write that survives →](2-writes.md)*

You will build two things: a tiny API, and a console app that talks to it. First without Hyperwyc, so you can see what normally happens when the API goes away — then with it, so you can see the difference is two registrations.

## The API

```bash
dotnet new web -o TutorialApi
cd TutorialApi
```

Replace `Program.cs` with this:

```csharp
var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/products", () => new[]
{
    new { Id = 1, Name = "Anvil", Price = 42.00m },
    new { Id = 2, Name = "Rocket skates", Price = 199.99m },
});

app.Run("http://localhost:5199");
```

```bash
dotnet run
```

Leave it running. Everything from here happens in a second terminal.

## The client, without Hyperwyc

```bash
dotnet new console -o TutorialClient
cd TutorialClient
dotnet add package Microsoft.Extensions.Http
```

```csharp
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();
services.AddHttpClient("MyApi", c => c.BaseAddress = new Uri("http://localhost:5199"));

var provider = services.BuildServiceProvider();
var http = provider.GetRequiredService<IHttpClientFactory>().CreateClient("MyApi");

var products = await http.GetFromJsonAsync<List<Product>>("/products");
Console.WriteLine($"{products?.Count} products, first = {products?[0].Name}");

record Product(int Id, string Name, decimal Price);
```

```bash
dotnet run
```

```
2 products, first = Anvil
```

## Now take the API away

Go back to the first terminal and stop the API with `Ctrl+C`. Then run the client again:

```
Unhandled exception. System.Net.Http.HttpRequestException: Connection refused (localhost:5199)
 ---> System.Net.Sockets.SocketException (111): Connection refused
```

Nothing surprising — there is nothing on the other end of the socket. **This is the line you would otherwise be writing a `try`/`catch` around**, in every place your app reads from the API, along with whatever you decided to show the user instead.

Remember what this looks like. It is the thing that stops happening.

## Add Hyperwyc

```bash
dotnet add package Hyperwyc
```

```diff
  using System.Net.Http.Json;
+ using Hyperwyc;
  using Microsoft.Extensions.DependencyInjection;

  var services = new ServiceCollection();
- services.AddHttpClient("MyApi", c => c.BaseAddress = new Uri("http://localhost:5199"));
+ services.AddHttpClient("MyApi", c => c.BaseAddress = new Uri("http://localhost:5199"))
+     .AddHyperwycHandler();
+
+ services.AddHyperwyc();
```

Two registrations and a `using`. **Nothing below them changes** — `GetFromJsonAsync` is the same call it was, and it still does not know Hyperwyc is there.

Start the API again and run the client:

```
2 products, first = Anvil
```

Same as before. The interesting part is invisible: the response was also written to a durable, encrypted store on its way past.

## Take the API away again

`Ctrl+C` the API, and run the client once more:

```
2 products, first = Anvil
```

**No exception.** The request was attempted, the connection was refused exactly as it was the first time, and Hyperwyc answered from what it stored — with the same line of calling code that threw two minutes ago.

## What about a route you never fetched?

Add this and run it again, still with the API down:

```csharp
var response = await http.GetAsync("/nothing-cached");
Console.WriteLine($"{(int)response.StatusCode} " +
    $"{response.Headers.GetValues("X-Hyperwyc-Status").First()} " +
    $"body={await response.Content.ReadAsStringAsync()}");
```

```
200 Offline body=null
```

A `200`, not a `404` and not an exception. Hyperwyc has no data for that route and says so by returning nothing — which is a case your code already handles, because a search with no matches looks the same. The `X-Hyperwyc-Status` header is there if you want to know *why* it was empty. [Why a `200`](../design.md#why-a-200-and-not-a-404) is on the design page; the codes and headers are in [Synthetic responses](../responses.md).

## What you just proved

- The same `GET` that threw `HttpRequestException` now returns data with the API stopped.
- A `GET` with nothing stored returns an empty result rather than throwing.
- The only thing that changed between those two runs was two registration lines. You have the diff above; the call site is untouched.

Reads are the easy half, because a read can always be degraded safely — the worst case is data slightly older than you wanted. Writes cannot be degraded that way: a write that never arrives is work the user has lost. That is next.

*Next: [2. A write that survives →](2-writes.md)*
