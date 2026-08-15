using Hyperwyc.Sample.ApiService.Persistence;
using Microsoft.EntityFrameworkCore;
using Shared;

namespace Hyperwyc.Sample.ApiService.Services;

public class SalesService(ApplicationDbContext context)
{
    public async Task Clear(CancellationToken token)
    {
        context.Sales.RemoveRange(context.Sales);
        await context.SaveChangesAsync(token);
    }

    public Task<List<Sale>> GetAll(CancellationToken token) => context.Sales.ToListAsync(token);

    public async Task<Sale?> RecordSale(Sale sale, CancellationToken token)
    {
        var product = context.Products.FirstOrDefault(p => p.Id == sale.ProductId);

        if (product is null)
            return null;

        if (product.StockLevel < sale.Quantity)
        {
            throw new ArgumentOutOfRangeException($"Only {product.StockLevel} of '{product.Name}' left in stock.");
        }

        product.StockLevel -= sale.Quantity;

        var recorded = new Sale
        {
            Id          = sale.Id == Guid.Empty ? Guid.NewGuid() : sale.Id,
            ProductId   = sale.ProductId,
            Quantity    = sale.Quantity,
            SoldAt      = DateTime.UtcNow,
        };

        context.Sales.Add(recorded);

        await context.SaveChangesAsync(token);

        return recorded;
    }
}
