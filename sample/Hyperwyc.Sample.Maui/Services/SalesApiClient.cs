using System.Net.Http.Json;
using Shared;

namespace Hyperwyc.Sample.Maui.Services;

public class SalesApiClient(HttpClient client)
{
    public async Task<List<Sale>> GetSalesAsync()
    {
        var sales = await client.GetFromJsonAsync<List<Sale>>("/sales", options: JsonOptions.GlobalOptions);
        return sales ?? [];
    }

    public async Task<Sale?> RecordSaleAsync(Sale sale)
    {
        // record the sale

        var result = await client.PostAsJsonAsync("/sales", sale);

        result.EnsureSuccessStatusCode();
        var resultObj = await result.Content.ReadFromJsonAsync<Sale>();

        // generate and upload receipt
        await using var stream = await PdfService.GeneratePdfAsync(sale);
        using var content = new MultipartFormDataContent();
        var streamContent = new StreamContent(stream);
        streamContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf");
        content.Add(streamContent, "file", $"{sale.Id}.pdf"); // filename gets set on server but this matches anyway

        using var response = await client.PostAsync($"/sales/{sale.Id}/receipt", content);

        return resultObj;
    }


}
