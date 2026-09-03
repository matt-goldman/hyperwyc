using Hyperwyc.Sample.ApiService.Persistence;
using Hyperwyc.Sample.ApiService.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Shared;

var builder = WebApplication.CreateBuilder(args);

// Add service defaults & Aspire client integrations.
builder.AddServiceDefaults();

// Add services to the container.
builder.Services.AddProblemDetails();

builder.AddSqlServerDbContext<ApplicationDbContext>(connectionName: "database");

builder.Services.AddScoped<ProductService>();
builder.Services.AddScoped<SalesService>();

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

app.MapGet("/", () => Results.Ok(new
{
    service = "Hyperwyc sample API",
    endpoints = new[]
    {
        "GET  /products              — the catalogue",
        "GET  /products/{id}         — a single product",
        "POST /products/regenerate   — build a brand new catalogue (for testing stale caches)",
        "GET  /sales                 — sales recorded so far",
        "POST /sales                 — record a sale; deduplicates on the client-supplied Sale.Id",
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
.WithName("RegenerateCatalogue")
.RequireAuthorization();

app.MapGet("/sales", async ([FromServices] SalesService service, CancellationToken token) =>
{
    var sales = await service.GetAll(token);
    return Results.Ok(sales.OrderByDescending(s => s.SoldAt).ToList());
})
.WithName("GetSales")
.RequireAuthorization();

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
            : Results.Created($"/sales/{result.Id}", result); // todo: this endpoint doesn't exist yet
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
.WithName("RecordSale")
.RequireAuthorization();

app.MapPost("/sales/{id:guid}/receipt", async (
        Guid id,
        HttpRequest request,
        CancellationToken token) =>
    {
        // don't bother checking the DB, it's just a demo
        if (!request.HasFormContentType)
            return Results.BadRequest("Expected multipart/form-data.");

        var form = await request.ReadFormAsync(token);
        var file = form.Files.GetFile("file");

        if (file is null)
            return Results.BadRequest("No file provided.");

        var filePath = Path.Combine(Directory.GetCurrentDirectory(), $"{id}.pdf");

        await using var fileStream = new FileStream(filePath, FileMode.Create);
        await file.CopyToAsync(fileStream,  token);

        return Results.Ok();
    })
    .WithName("StoreReceipt")
    .RequireAuthorization();

app.MapGet("/sales/{id:guid}/receipt", async (Guid id, CancellationToken token) =>
    {
        // don't bother checking the DB, it's just a demo

        var filePath = Path.Combine(Directory.GetCurrentDirectory(), $"{id}.pdf");

        if (!File.Exists(filePath))
        {
            return Results.NotFound($"No receipt for {id} was found.");
        }

        await using var fileStream = new FileStream(filePath, FileMode.Open);
        return Results.File(fileStream, "application/pdf");
    })
    .WithName("GetReceipt")
    .RequireAuthorization();

app.MapDefaultEndpoints();

// run migrations

using var scope = app.Services.CreateScope();
var services = scope.ServiceProvider;
var context = services.GetRequiredService<ApplicationDbContext>();
await context.Database.MigrateAsync();

app.Run();
