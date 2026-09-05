using Hyperwyc.Models;
using Xunit;

namespace Hyperwyc.Tests;

/// <summary>
/// Covers issue #22's matcher: first registered match wins, on the path only.
/// </summary>
public class RoutePolicyMapTests
{
    private static RoutePolicy Resolve(RoutePolicyMap map, string url) =>
        map.PolicyFor(new Uri(url));

    [Fact]
    public void NoPatterns_ReturnsTheDefault()
    {
        var map = new RoutePolicyMap();

        Assert.Same(map.Default, Resolve(map, "https://example.com/api/anything"));
    }

    [Fact]
    public void NoMatch_ReturnsTheDefault()
    {
        var map = new RoutePolicyMap().For("/api/sales/*", RoutePolicy.NetworkOnly());

        Assert.Equal(SourcePriority.CacheFirst, Resolve(map, "https://example.com/other").SourcePriority);
    }

    [Fact]
    public void ExactPattern_MatchesOnlyThatPath()
    {
        var map = new RoutePolicyMap().For("/api/sales", RoutePolicy.NetworkOnly());

        Assert.Equal(SourcePriority.NetworkOnly, Resolve(map, "https://example.com/api/sales").SourcePriority);
        Assert.Equal(SourcePriority.CacheFirst, Resolve(map, "https://example.com/api/sales/7").SourcePriority);
    }

    [Fact]
    public void WildcardPattern_MatchesTheCollectionAndBeneath()
    {
        var map = new RoutePolicyMap().For("/api/sales/*", RoutePolicy.NetworkOnly());

        // The collection itself, not just its children — a separate entry for "/api/sales"
        // would be a papercut with no upside.
        Assert.Equal(SourcePriority.NetworkOnly, Resolve(map, "https://example.com/api/sales").SourcePriority);
        Assert.Equal(SourcePriority.NetworkOnly, Resolve(map, "https://example.com/api/sales/7").SourcePriority);
        Assert.Equal(SourcePriority.NetworkOnly, Resolve(map, "https://example.com/api/sales/7/lines").SourcePriority);

        // But not a sibling that merely shares a prefix string.
        Assert.Equal(SourcePriority.CacheFirst, Resolve(map, "https://example.com/api/salesfigures").SourcePriority);
    }

    [Fact]
    public void BareStar_MatchesEverything()
    {
        var map = new RoutePolicyMap().For("*", RoutePolicy.NetworkOnly());

        Assert.Equal(SourcePriority.NetworkOnly, Resolve(map, "https://example.com/anything/at/all").SourcePriority);
    }

    // -------------------------------------------------------------------------
    // Later rules refine earlier ones — the whole mechanism
    // -------------------------------------------------------------------------

    [Fact]
    public void GeneralThenSpecific_TheSpecificRuleRefinesTheGeneralOne()
    {
        // The documented order: state the general rule, then carve out the exception.
        var map = new RoutePolicyMap()
            .For("/api/*", RoutePolicy.CacheFirst(TimeSpan.FromHours(1)))
            .For("/api/sales/*", RoutePolicy.NetworkOnly());

        Assert.Equal(SourcePriority.NetworkOnly, Resolve(map, "https://example.com/api/sales/7").SourcePriority);

        // And the general rule still covers everything the exception does not.
        Assert.Equal(TimeSpan.FromHours(1), Resolve(map, "https://example.com/api/products").Ttl);
    }

    [Fact]
    public void SpecificThenGeneral_TheBroadRuleOverridesTheNarrowOne()
    {
        // The failure mode, pinned so the guidance stays honest: a broad rule registered after
        // a narrow one overrides it. Inspectable in the source, which is the trade this makes
        // against resolving by computed specificity.
        var map = new RoutePolicyMap()
            .For("/api/sales/*", RoutePolicy.NetworkOnly())
            .For("/api/*", RoutePolicy.CacheFirst(TimeSpan.FromHours(1)));

        Assert.Equal(SourcePriority.CacheFirst, Resolve(map, "https://example.com/api/sales/7").SourcePriority);
    }

    [Fact]
    public void ThreeLevels_ResolveToTheDeepestRefinement()
    {
        var map = new RoutePolicyMap()
            .For("/api/*", RoutePolicy.CacheFirst(TimeSpan.FromHours(1)))
            .For("/api/sales/*", RoutePolicy.NetworkFirst(TimeSpan.FromSeconds(30)))
            .For("/api/sales/draft", RoutePolicy.NetworkOnly());

        Assert.Equal(SourcePriority.NetworkOnly, Resolve(map, "https://example.com/api/sales/draft").SourcePriority);
        Assert.Equal(SourcePriority.NetworkFirst, Resolve(map, "https://example.com/api/sales/7").SourcePriority);
        Assert.Equal(SourcePriority.CacheFirst, Resolve(map, "https://example.com/api/products").SourcePriority);
    }

    [Fact]
    public void RegisteringTheSamePatternTwice_TheLaterOneApplies()
    {
        // Falls out of the refinement model rather than being a special case: re-stating a
        // rule replaces it.
        var map = new RoutePolicyMap()
            .For("/api/*", RoutePolicy.CacheFirst(TimeSpan.FromHours(1)))
            .For("/api/*", RoutePolicy.NetworkOnly());

        Assert.Equal(SourcePriority.NetworkOnly, Resolve(map, "https://example.com/api/products").SourcePriority);
    }

    // -------------------------------------------------------------------------
    // What is matched on
    // -------------------------------------------------------------------------

    [Fact]
    public void MatchingIgnoresSchemeHostAndPort()
    {
        var map = new RoutePolicyMap().For("/api/sales/*", RoutePolicy.NetworkOnly());

        // A pattern works regardless of the client's BaseAddress.
        Assert.Equal(SourcePriority.NetworkOnly, Resolve(map, "http://localhost:5401/api/sales/7").SourcePriority);
        Assert.Equal(SourcePriority.NetworkOnly, Resolve(map, "https://api.example.com/api/sales/7").SourcePriority);
    }

    [Fact]
    public void MatchingIgnoresQueryString()
    {
        var map = new RoutePolicyMap().For("/api/sales", RoutePolicy.NetworkOnly());

        Assert.Equal(SourcePriority.NetworkOnly, Resolve(map, "https://example.com/api/sales?page=2").SourcePriority);
    }

    [Fact]
    public void MatchingIsCaseInsensitive()
    {
        // Getting this wrong by hand is silent, and is much of the reason to ship a matcher
        // rather than document a ten-line one.
        var map = new RoutePolicyMap().For("/API/Sales/*", RoutePolicy.NetworkOnly());

        Assert.Equal(SourcePriority.NetworkOnly, Resolve(map, "https://example.com/api/sales/7").SourcePriority);
    }

    [Fact]
    public void NullUrl_ReturnsTheDefault()
    {
        var map = new RoutePolicyMap().For("*", RoutePolicy.NetworkOnly());

        // "*" matches everything including a request with no URI, which is degenerate but
        // must not throw.
        Assert.Equal(SourcePriority.NetworkOnly, map.PolicyFor(null).SourcePriority);
    }

    // -------------------------------------------------------------------------
    // Guards
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyPattern_Throws(string pattern) =>
        Assert.Throws<ArgumentException>(() => new RoutePolicyMap().For(pattern, RoutePolicy.CacheFirst()));

    [Fact]
    public void NullPolicy_Throws() =>
        Assert.Throws<ArgumentNullException>(() => new RoutePolicyMap().For("/api/*", null!));

    // -------------------------------------------------------------------------
    // Factories
    // -------------------------------------------------------------------------

    [Fact]
    public void NetworkOnly_DisablesInvalidationToo()
    {
        // Nothing is stored under NetworkOnly, so there is nothing to invalidate; leaving it
        // on would be a write to the store on a route that opted out of the store.
        Assert.False(RoutePolicy.NetworkOnly().InvalidateCacheOnWrite);
    }

    [Fact]
    public void FactoriesCarryTheirStrategyAndDefaultTtl()
    {
        Assert.Equal(SourcePriority.CacheFirst, RoutePolicy.CacheFirst().SourcePriority);
        Assert.Equal(SourcePriority.NetworkFirst, RoutePolicy.NetworkFirst().SourcePriority);
        Assert.Equal(SourcePriority.NetworkOnly, RoutePolicy.NetworkOnly().SourcePriority);

        Assert.Equal(TimeSpan.FromDays(1), RoutePolicy.CacheFirst().Ttl);
        Assert.Equal(TimeSpan.FromDays(7), RoutePolicy.CacheFirst(TimeSpan.FromDays(7)).Ttl);
    }

    [Fact]
    public void RecordSyntaxComposesOverAFactory()
    {
        var policy = RoutePolicy.CacheFirst(TimeSpan.FromDays(7)) with { InvalidateCacheOnWrite = false };

        Assert.Equal(TimeSpan.FromDays(7), policy.Ttl);
        Assert.False(policy.InvalidateCacheOnWrite);
        Assert.Equal(SourcePriority.CacheFirst, policy.SourcePriority);
    }
}
