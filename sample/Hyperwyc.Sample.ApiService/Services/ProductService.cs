using Hyperwyc.Sample.ApiService.Persistence;
using Microsoft.EntityFrameworkCore;
using Shared;

namespace Hyperwyc.Sample.ApiService.Services;

public class ProductService(ApplicationDbContext context)
{
    public async Task<List<Product>> GetProducts(CancellationToken token)
    {
        var products = await context.Products.ToListAsync(token);

        if (products.Count != 0) return products;

        await Regenerate(token);

        products = await context.Products.ToListAsync(token);
        return products;
    }

    public async Task<Product?> GetProduct(int id, CancellationToken token)
    {
        var product = await context.Products.FindAsync(id, token);
        return product;
    }

    public async Task Regenerate(CancellationToken token)
    {
        var products = await GetProducts(token);
        var catalogue = new Catalogue();
        context.Products.RemoveRange(products);
        context.Products.AddRange(catalogue.Products);
        await context.SaveChangesAsync(token);
    }
}

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

