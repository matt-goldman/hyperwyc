var builder = DistributedApplication.CreateBuilder(args);

var apiService = builder.AddProject<Projects.Hyperwyc_Sample_ApiService>("apiservice")
    .WithHttpHealthCheck("/health");

var publicDevTunnel = builder.AddDevTunnel("devtunnel-public")
    .WithAnonymousAccess()
    .WithReference(apiService.GetEndpoint("https"));

var maui = builder.AddMauiProject("mauiapp", "../Hyperwyc.Sample.Maui/Hyperwyc.Sample.Maui.csproj");

// Sample uses Android only as it's the only target that
// can be run across Windows, macOS, and Linux dev/host
// environments
maui.AddAndroidEmulator()
    .WithOtlpDevTunnel()
    .WithReference(apiService, publicDevTunnel);

builder.Build().Run();
