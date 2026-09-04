using Hyperwyc.Models;

namespace Hyperwyc.Tests.Fakes;

/// <summary>
/// Options shaped for a test. TTL and strategy live on the resolved <see cref="RoutePolicy"/>
/// now, so a test that wants either sets the route map's default.
/// </summary>
internal static class TestOptions
{
    /// <summary>A zero TTL makes every cached entry stale, which is how a refetch is forced.</summary>
    public static HyperwycOptions WithTtl(TimeSpan ttl)
    {
        var options = new HyperwycOptions();
        options.Routes.Default = RoutePolicy.CacheFirst(ttl);
        return options;
    }
}
