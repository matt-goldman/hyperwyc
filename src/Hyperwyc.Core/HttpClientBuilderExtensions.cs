using Microsoft.Extensions.DependencyInjection;
using Hyperwyc.Interfaces;

namespace Hyperwyc;

/// <summary>
/// Extension methods for adding <see cref="HyperwycHandler"/> to a named
/// <see cref="HttpClient"/>.
/// </summary>
public static class HttpClientBuilderExtensions
{
    /// <summary>
    /// Adds <see cref="HyperwycHandler"/> to the client's pipeline, capturing the
    /// client's name so queued writes can be replayed back through this same pipeline.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Prefer this over <c>AddHttpMessageHandler&lt;HyperwycHandler&gt;()</c>. Without
    /// the client name, Hyperwyc cannot tell which pipeline a queued write came from
    /// and falls back to <see cref="HyperwycOptions.ReplayTransport"/> — which does not
    /// include the application's own handlers, so replays would miss auth.
    /// </para>
    /// <para>
    /// Register Hyperwyc's handler first, so that handlers added after it also run on
    /// replays:
    /// </para>
    /// <code>
    /// services.AddHttpClient("MyApi")
    ///     .AddHyperwycHandler()
    ///     .AddHttpMessageHandler&lt;AuthHandler&gt;();
    /// </code>
    /// </remarks>
    /// <param name="builder">The client builder to add the handler to.</param>
    /// <returns>The original <paramref name="builder"/> for chaining.</returns>
    public static IHttpClientBuilder AddHyperwycHandler(this IHttpClientBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // builder.Name is the only place the client's name is available, which is why
        // this has to be an extension on IHttpClientBuilder rather than a plain
        // registration.
        var clientName = builder.Name;

        return builder.AddHttpMessageHandler(sp => new HyperwycHandler(
            sp.GetRequiredService<ISyncStore>(),
            sp.GetRequiredService<IConnectivityService>(),
            sp.GetRequiredService<ISyncPolicy>(),
            sp.GetRequiredService<SyncEventStream>(),
            sp.GetRequiredService<HyperwycOptions>(),
            clientName));
    }
}
