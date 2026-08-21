using System.Text.Json;

namespace Hyperwyc.Sample.Maui.Services;

public static class JsonOptions
{
    public static readonly JsonSerializerOptions GlobalOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
}
