using CommunityToolkit.Maui;
using Hyperwyc.Interfaces;
using Hyperwyc.Sample.Maui.Pages;
using Hyperwyc.Sample.Maui.Services;
using Hyperwyc.Sample.Maui.ViewModels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using Plugin.Maui.SmartNavigation;

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
            })
            .UseSmartNavigation()
            .UseMauiCommunityToolkit();

        builder.AddServiceDefaults();

        // Connectivity is the one thing Hyperwyc cannot decide for you. Registering it is
        // all that is needed — order relative to AddHyperwyc does not matter.
        builder.Services.AddSingleton<IConnectivityService, MauiConnectivityService>();
        builder.Services.AddHyperwyc();

        // MAUI integration with Aspire doesn't currently work.
        // Specific issue currently blocking this:
        // https://github.com/microsoft/aspire/pull/19383
        // Other fixes pending but this is the one that should
        // unblock this and let Aspire actually start the MAUI
        // app. After that, remains to be seen what other fixes
        // will be required. Once it's working you can use Aspire
        // here, and change the base address on all HTTPClients
        // to `new Uri("https+http://aspireservice");`. In the
        // meantime, start your AppHost (and devtunnel separately
        // if needed) and set the address here. Alternatively
        // as we are only using the Android emulator we should
        // be able to use the special host address. Just can't
        // use it easily with HTTPS, but that's not a blocker.
        const string apiAddress = "http://10.0.2.2:5401";

        builder.Services.AddTransient<AuthHandler>();

        builder.Services.AddHttpClient<ProductsApiClient>(client =>
        {
            client.BaseAddress = new Uri(apiAddress);
        })
        .AddHyperwycHandler()
        .AddHttpMessageHandler<AuthHandler>();

        builder.Services.AddHttpClient<SalesApiClient>(client =>
        {
            client.BaseAddress = new Uri(apiAddress);
        })
        .AddHyperwycHandler()
        .AddHttpMessageHandler<AuthHandler>();

        builder.Services.AddHttpClient(nameof(AuthenticationService), client =>
        {
            client.BaseAddress = new Uri(apiAddress);
        });

        builder.Services.AddSingleton<AuthenticationService>();

        builder.Services.AddSingleton<CatalogueViewModel>();
        builder.Services.AddSingleton<LoginViewModel>();
        builder.Services.AddSingleton<NewSaleViewModel>();
        builder.Services.AddSingleton<SalesViewModel>();
        builder.Services.AddSingleton<LiveEventsViewModel>();
        builder.Services.AddSingleton<DiagnosticsViewModel>();

        builder.Services.AddSingleton<CataloguePage>();
        builder.Services.AddSingleton<LoginPage>();
        builder.Services.AddSingleton<NewSalePage>();
        builder.Services.AddSingleton<SalesPage>();
        builder.Services.AddSingleton<LiveEventsPage>();
        builder.Services.AddSingleton<DiagnosticsPage>();

#if DEBUG
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
