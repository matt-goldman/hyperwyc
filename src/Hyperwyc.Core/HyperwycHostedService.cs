using Microsoft.Extensions.Hosting;
using Hyperwyc.Interfaces;

namespace Hyperwyc;

/// <summary>
/// An <see cref="IHostedService"/> that triggers an outbox flush on application
/// startup when <see cref="HyperwycOptions.FlushOnStartup"/> is <see langword="true"/>
/// and the device is currently online.
/// </summary>
internal sealed class HyperwycHostedService(
    SyncOrchestrator orchestrator,
    IConnectivityService connectivity) : IHostedService
{
    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (connectivity.IsConnected)
            return orchestrator.FlushAsync(cancellationToken);

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
