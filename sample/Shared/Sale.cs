namespace Shared;

public class Sale
{
    public Guid Id { get; set; } =  Guid.NewGuid();

    public DateTime SoldAt { get; set; } = DateTime.UtcNow;

    public int ProductId { get; set; }

    public int Quantity { get; set; }
}
