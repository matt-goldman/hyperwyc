using Aspire.Hosting.DevTunnels;

var builder = DistributedApplication.CreateBuilder(args);

var sql = builder.AddSqlServer("sql")
    .WithLifetime(ContainerLifetime.Persistent);

var db = sql.AddDatabase("database");

var apiService = builder.AddProject<Projects.Hyperwyc_Sample_ApiService>("apiservice")
    .WithHttpHealthCheck("/health")
    .WithReference(db)
    .WaitFor(db);

// None of this works reliably at the moment, so just using local address for now
// var publicDevTunnel = builder.AddDevTunnel("devtunnel-public")
//     .WithAnonymousAccess()
//     .WithReference(apiService.GetEndpoint("http"));

var maui = builder.AddMauiProject("mauiapp", "../Hyperwyc.Sample.Maui/Hyperwyc.Sample.Maui.csproj");

// Sample uses Android only as it's the only target that
// can be run across Windows, macOS, and Linux dev/host
// environments, also lets you use the special host
// IP for the emulator and skip tunnel dramas.
maui.AddAndroidEmulator()
    .WithOtlpDevTunnel();
    //.WithReference(apiService, publicDevTunnel);

builder.Build().Run();
