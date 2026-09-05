using Hyperwyc.Models;

namespace Hyperwyc;

/// <summary>
/// Maps URL patterns to <see cref="RoutePolicy"/> values.
/// </summary>
/// <remarks>
/// <para>
/// <b>Register from general to specific. Each rule refines the ones before it.</b>
/// </para>
/// <code>
/// options.Routes
///     .For("/api/*",                RoutePolicy.CacheFirst(TimeSpan.FromHours(1)))
///     .For("/api/sales/*",          RoutePolicy.NetworkFirst(TimeSpan.FromSeconds(30)))
///     .For("/api/sales/{id}/lines", RoutePolicy.NetworkOnly());
/// </code>
/// <para>
/// Written out that forms a pyramid — shortest line at the top, widening as each rule
/// narrows — which is both the order you think in —
/// state the general rule, then carve out the exceptions — and a shape you can check at a
/// glance. Where two patterns both match, the one registered later applies, so a broad rule
/// placed <em>after</em> a narrow one will override it.
/// </para>
/// <para>
/// This is <c>.gitignore</c>'s model and the CSS cascade's: a later rule refines an earlier one.
/// The alternative, resolving by computed specificity, is an invisible rule a reader has to know
/// and a maintainer has to implement — and with the pattern language here it would buy nothing,
/// since any two patterns that both match a path necessarily nest, so "more specific" is just
/// "registered later" spelled less clearly.
/// </para>
/// </remarks>
public sealed class RoutePolicyMap
{
    private readonly List<(string Pattern, RoutePolicy Policy)> _routes = [];

    /// <summary>
    /// The policy for requests matching no pattern. Defaults to
    /// <see cref="RoutePolicy.CacheFirst()"/>.
    /// </summary>
    public RoutePolicy Default { get; set; } = RoutePolicy.CacheFirst();

    /// <summary>
    /// Applies <paramref name="policy"/> to requests whose path matches
    /// <paramref name="pattern"/>, refining any rule registered before it.
    /// </summary>
    /// <param name="pattern">
    /// A path, optionally ending in <c>/*</c> to include everything beneath it. <c>*</c> alone
    /// matches every request. Matched against the URL <b>path</b> only — scheme, host, port and
    /// query string are ignored, so a pattern works regardless of the client's
    /// <see cref="HttpClient.BaseAddress"/>. Case-insensitive.
    /// </param>
    /// <param name="policy">The policy to apply.</param>
    /// <returns>This map, for chaining.</returns>
    public RoutePolicyMap For(string pattern, RoutePolicy policy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        ArgumentNullException.ThrowIfNull(policy);

        _routes.Add((pattern, policy));
        return this;
    }

    /// <summary>
    /// The most recently registered policy whose pattern matches <paramref name="url"/>, or
    /// <see cref="Default"/>.
    /// </summary>
    public RoutePolicy PolicyFor(Uri? url)
    {
        var path = url is null
            ? string.Empty
            : url.IsAbsoluteUri ? url.AbsolutePath : url.OriginalString.Split('?')[0];

        // Walked backwards so a later registration refines an earlier one. The list keeps
        // source order rather than being built reversed, so anything that reads it — a
        // diagnostics view, a debugger — sees what the developer wrote.
        for (var i = _routes.Count - 1; i >= 0; i--)
        {
            var (pattern, policy) = _routes[i];
            if (Matches(pattern, path))
                return policy;
        }

        return Default;
    }

    private static bool Matches(string pattern, string path)
    {
        if (pattern == "*")
            return true;

        if (!pattern.EndsWith("/*", StringComparison.Ordinal))
            return string.Equals(pattern, path, StringComparison.OrdinalIgnoreCase);

        // "/api/sales/*" covers "/api/sales" as well as everything beneath it. Requiring a
        // separate entry for the collection itself would be a papercut with no upside.
        var prefix = pattern[..^2];
        return string.Equals(prefix, path, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase);
    }
}
