using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Shared;

namespace Hyperwyc.Sample.ApiService.Persistence;

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
    : IdentityDbContext<IdentityUser>(options)
{
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Sale> Sales => Set<Sale>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        // Both keys are assigned by the code that creates the entity, not by the store:
        // the catalogue numbers products 1..N on every regeneration, and the client
        // supplies Sale.Id so POST /sales can deduplicate a retried request.
        builder.Entity<Product>()
            .Property(p => p.Id)
            .ValueGeneratedNever();

        builder.Entity<Sale>()
            .Property(s => s.Id)
            .ValueGeneratedNever();

        builder.Entity<Product>()
            .Property(p => p.Price)
            .HasPrecision(18, 2);

        base.OnModelCreating(builder);
    }
}
