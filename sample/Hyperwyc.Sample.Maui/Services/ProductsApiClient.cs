using System.Net.Http.Json;
using Shared;

namespace Hyperwyc.Sample.Maui.Services;

public class ProductsApiClient(HttpClient client)
{
    public async Task<List<Product>> GetProductsAsync()
    {
        var products = await client.GetFromJsonAsync<List<Product>>("/products", options: JsonOptions.GlobalOptions);
        return products ?? [];
    }

    public async Task<Product?> GetProductAsync(int id)
    {
        var product = await client.GetFromJsonAsync<Product>($"/products/{id}");
        return product;
    }

    public Task RegenerateCatalogAsync()
        => client.PostAsync("/products/regenerate", null);
}
