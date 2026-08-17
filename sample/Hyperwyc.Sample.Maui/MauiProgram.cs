using Hyperwyc.Sample.Maui.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;

namespace Hyperwyc.Sample.Maui;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            });

        builder.AddServiceDefaults();

        builder.Services.AddHyperwyc(configure: config =>
        {
            config.Connectivity = new MauiConnectivityService();
        });

        builder.Services.AddHttpClient<ProductsApiClient>(client =>
        {
            client.BaseAddress = new Uri("https+http://apiservice");
        })
        .AddHyperwycHandler()
        .AddHttpMessageHandler<AuthHandler>();

        builder.Services.AddHttpClient<SalesApiClient>(client =>
        {
            client.BaseAddress = new Uri("https+http://apiservice");
        })
        .AddHyperwycHandler()
        .AddHttpMessageHandler<AuthHandler>();

        builder.Services.AddHttpClient<AuthenticationService>(client =>
        {
            client.BaseAddress = new Uri("https+http://apiservice");
        });

        builder.Services.AddSingleton<ProductsApiClient>();
        builder.Services.AddSingleton<SalesApiClient>();
        builder.Services.AddSingleton<AuthenticationService>();

#if DEBUG
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
