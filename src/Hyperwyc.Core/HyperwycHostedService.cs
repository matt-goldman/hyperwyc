using Microsoft.Extensions.Hosting;
using Hyperwyc.Interfaces;

namespace Hyperwyc;

/// <summary>
/// An <see cref="IHostedService"/> that triggers an outbox flush on application
/// startup when <see cref="HyperwycOptions.FlushOnStartup"/> is <see langword="true"/>
/// and the device is currently online.
/// </summary>
/// <remarks>
/// Registered only when the option is on, and it is off by default. The flush completes inside
/// host startup, so anything that subscribes to <see cref="IHyperwyc.Events"/> afterwards misses
/// what it delivered — and since a delivery is reported nowhere else (ADR 0010), that outcome is
/// gone. See <see cref="HyperwycOptions.FlushOnStartup"/>.
/// </remarks>
internal sealed class HyperwycHostedService(
    OutboxProcessor processor,
    IConnectivityService connectivity) : IHostedService
{
    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (connectivity.IsConnected)
            return processor.FlushAsync(cancellationToken);

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
