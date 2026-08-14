using System.Collections.Concurrent;
using Shared;

var builder = WebApplication.CreateBuilder(args);

// Add service defaults & Aspire client integrations.
builder.AddServiceDefaults();

// Add services to the container.
builder.Services.AddProblemDetails();

// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

var app = builder.Build();

// Configure the HTTP request pipeline.
app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// -----------------------------------------------------------------------------
// In-memory state
//
// Deliberately not a database: the point of this sample is Hyperwyc's behaviour,
// not the API's. Everything resets when the process restarts, which is itself
// useful — a client holding a cached catalogue will keep showing the old one.
// -----------------------------------------------------------------------------

var catalogue = new Catalogue();
var sales = new List<Sale>();

// Idempotency-Key -> the sale that key already produced. Hyperwyc injects this
// header on every mutating request and reuses the same value when it replays, so
// a write that was delivered but whose response never made it back does not get
// recorded twice.
var salesByIdempotencyKey = new ConcurrentDictionary<string, Sale>(StringComparer.Ordinal);

var mutationLock = new object();

// -----------------------------------------------------------------------------
// Endpoints
// -----------------------------------------------------------------------------

app.MapGet("/", () => Results.Ok(new
{
    service = "Hyperwyc sample API",
    endpoints = new[]
    {
        "GET  /products              — the catalogue",
        "GET  /products/{id}         — a single product",
        "POST /products/regenerate   — build a brand new catalogue (for testing stale caches)",
        "GET  /sales                 — sales recorded so far",
        "POST /sales                 — record a sale; honours Idempotency-Key",
    },
}));

app.MapGet("/products", () => Results.Ok(catalogue.Products))
    .WithName("GetProducts");

app.MapGet("/products/{id:int}", (int id) =>
{
    var product = catalogue.Products.FirstOrDefault(p => p.Id == id);
    return product is null ? Results.NotFound() : Results.Ok(product);
})
.WithName("GetProduct");

// Rebuilds the catalogue from scratch while the app is running. Restarting the API
// would do the same, but this lets a client be caught holding a stale cache without
// stopping anything — which is the behaviour worth demonstrating.
app.MapPost("/products/regenerate", () =>
{
    lock (mutationLock)
    {
        catalogue.Regenerate();
        sales.Clear();
        salesByIdempotencyKey.Clear();
    }

    return Results.Ok(catalogue.Products);
})
.WithName("RegenerateCatalogue");

app.MapGet("/sales", () =>
{
    lock (mutationLock)
    {
        return Results.Ok(sales.OrderByDescending(s => s.SoldAt).ToList());
    }
})
.WithName("GetSales");

app.MapPost("/sales", (Sale sale, HttpRequest request) =>
{
    // Hyperwyc injects Idempotency-Key on mutating requests and reuses it across
    // replays. Returning the original result for a repeated key is what makes an
    // offline queue safe to flush more than once.
    var idempotencyKey = request.Headers["Idempotency-Key"].FirstOrDefault();

    if (!string.IsNullOrEmpty(idempotencyKey) &&
        salesByIdempotencyKey.TryGetValue(idempotencyKey, out var alreadyRecorded))
    {
        return Results.Ok(alreadyRecorded);
    }

    if (sale.Quantity <= 0)
        return Results.BadRequest(new { error = "Quantity must be greater than zero." });

    lock (mutationLock)
    {
        var product = catalogue.Products.FirstOrDefault(p => p.Id == sale.ProductId);

        if (product is null)
            return Results.NotFound(new { error = $"No product with id {sale.ProductId}." });

        if (product.StockLevel < sale.Quantity)
        {
            return Results.Conflict(new
            {
                error = $"Only {product.StockLevel} of '{product.Name}' left in stock.",
                available = product.StockLevel,
            });
        }

        product.StockLevel -= sale.Quantity;

        var recorded = new Sale
        {
            Id = sale.Id == Guid.Empty ? Guid.NewGuid() : sale.Id,
            ProductId = sale.ProductId,
            Quantity = sale.Quantity,
            SoldAt = DateTime.UtcNow,
        };

        sales.Add(recorded);

        if (!string.IsNullOrEmpty(idempotencyKey))
            salesByIdempotencyKey[idempotencyKey] = recorded;

        return Results.Created($"/sales/{recorded.Id}", recorded);
    }
})
.WithName("RecordSale");

app.MapDefaultEndpoints();

app.Run();

// -----------------------------------------------------------------------------
// Catalogue generation
// -----------------------------------------------------------------------------

/// <summary>
/// A randomly generated product catalogue, rebuilt on demand.
/// </summary>
/// <remarks>
/// Generated rather than seeded from a real products API so the sample has no
/// network dependency at startup and works offline — which matters, given the app
/// under test is an offline-first one.
/// </remarks>
internal sealed class Catalogue
{
    private static readonly string[] Qualities =
    [
        "Handcrafted", "Rustic", "Refined", "Sleek", "Vintage", "Ergonomic",
        "Practical", "Artisan", "Compact", "Heavy-duty", "Small-batch", "Modular",
    ];

    private static readonly string[] Materials =
    [
        "Copper", "Bamboo", "Walnut", "Linen", "Granite", "Brass",
        "Ceramic", "Leather", "Steel", "Cork", "Slate", "Cedar",
    ];

    private static readonly string[] Items =
    [
        "Kettle", "Lamp", "Stool", "Planter", "Mug", "Bookend",
        "Doorstop", "Tray", "Coaster Set", "Wall Clock", "Bread Bin", "Umbrella Stand",
    ];

    public List<Product> Products { get; private set; } = [];

    public Catalogue() => Regenerate();

    /// <summary>
    /// Replaces the catalogue with a fresh random one. Names, prices and stock all
    /// change, so a client comparing against a cached copy can see the difference.
    /// </summary>
    public void Regenerate()
    {
        var count = Random.Shared.Next(8, 16);
        var names = new HashSet<string>(StringComparer.Ordinal);

        // Sampling without replacement, so no two products share a name and the
        // client can tell them apart on screen.
        while (names.Count < count)
        {
            names.Add(string.Join(' ',
                Qualities[Random.Shared.Next(Qualities.Length)],
                Materials[Random.Shared.Next(Materials.Length)],
                Items[Random.Shared.Next(Items.Length)]));
        }

        Products = names
            .Select((name, index) => new Product
            {
                Id = index + 1,
                Name = name,
                Price = Math.Round((decimal)Random.Shared.NextDouble() * 90m + 5m, 2),
                StockLevel = Random.Shared.Next(0, 40),
            })
            .ToList();
    }
}
