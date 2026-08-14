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

        builder.Services.AddHttpClient<WeatherApiClient>(client => // need to create a client
        {
            client.BaseAddress = new Uri("https+http://apiservice"); // should probably be the tunnel name
        });

#if DEBUG
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}
}
