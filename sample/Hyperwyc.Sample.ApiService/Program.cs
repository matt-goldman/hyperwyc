using System.Collections.Concurrent;
using Hyperwyc.Sample.ApiService.Persistence;
using Hyperwyc.Sample.ApiService.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Shared;

var builder = WebApplication.CreateBuilder(args);

// Add service defaults & Aspire client integrations.
builder.AddServiceDefaults();

// Add services to the container.
builder.Services.AddProblemDetails();

builder.AddSqlServerDbContext<ApplicationDbContext>(connectionName: "database");

builder.Services.AddAuthorization();

builder.Services.AddIdentityApiEndpoints<IdentityUser>()
    .AddEntityFrameworkStores<ApplicationDbContext>();

// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

var app = builder.Build();

// Configure the HTTP request pipeline.
app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapIdentityApi<IdentityUser>();

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

app.MapGet("/products", async ([FromServices] ProductService service, CancellationToken token) =>
    {
        var products = await service.GetProducts(token);
        return Results.Ok(products);
    })
    .WithName("GetProducts");

app.MapGet("/products/{id:int}", async (int id, [FromServices] ProductService service, CancellationToken token) =>
{
    var product = await service.GetProduct(id, token);
    return product is null ? Results.NotFound() : Results.Ok(product);
})
.WithName("GetProduct");

// Rebuilds the catalogue from scratch while the app is running. Restarting the API
// would do the same, but this lets a client be caught holding a stale cache without
// stopping anything — which is the behaviour worth demonstrating.
app.MapPost("/products/regenerate", async (
        [FromServices] ProductService productService,
        [FromServices] SalesService salesService,
        CancellationToken token) =>
{
    await salesService.Clear(token);
    await productService.Regenerate(token);
    var products =  await productService.GetProducts(token);

    return Results.Ok(products);
})
.WithName("RegenerateCatalogue");

app.MapGet("/sales", async ([FromServices] SalesService service, CancellationToken token) =>
{
    var sales = await service.GetAll(token);
    return Results.Ok(sales.OrderByDescending(s => s.SoldAt).ToList());
})
.WithName("GetSales");

app.MapPost("/sales", async (
        [FromBody]Sale sale,
        [FromServices] SalesService service,
        HttpRequest request,
        CancellationToken token) =>
{
    if (sale.Quantity <= 0)
        return Results.BadRequest(new { error = "Quantity must be greater than zero." });

    try
    {
        var result = await service.RecordSale(sale, token);

        return result is null
            ? Results.NotFound(new { error = $"No product with id {sale.ProductId}." })
            : Results.Created($"/sales/{result.Id}", result);
    }
    catch (ArgumentOutOfRangeException e)
    {
        if (e.Message.Contains("stock", StringComparison.CurrentCultureIgnoreCase))
        {
            return Results.Conflict(new { error = e.Message });
        }
    }

    return Results.InternalServerError("An error occurred.");
})
.WithName("RecordSale");

app.MapDefaultEndpoints();

app.Run();
