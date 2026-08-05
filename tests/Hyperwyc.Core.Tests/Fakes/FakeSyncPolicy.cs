using Hyperwyc.Interfaces;
using Hyperwyc.Models;

namespace Hyperwyc.Tests.Fakes;

/// <summary>
/// Test double for <see cref="ISyncPolicy"/> with configurable behaviour.
/// </summary>
internal sealed class FakeSyncPolicy(
    CacheStrategy strategy = CacheStrategy.CacheFirst,
    bool shouldInvalidate = true,
    RetryOptions? retryOptions = null) : ISyncPolicy
{
    private static readonly RetryOptions DefaultRetryOptions =
        new(MaxRetries: 3, InitialDelay: TimeSpan.FromSeconds(1), BackoffMultiplier: 2.0);

    public CacheStrategy GetStrategy(HttpRequestMessage request) => strategy;

    public bool ShouldInvalidateCacheOnWrite(HttpRequestMessage request) => shouldInvalidate;

    public RetryOptions GetRetryOptions(HttpRequestMessage request) =>
        retryOptions ?? DefaultRetryOptions;
}
