using Restyc.Interfaces;
using Restyc.Models;

namespace Restyc.Tests.Fakes;

/// <summary>
/// Test double for <see cref="ISyncPolicy"/> with configurable behaviour.
/// </summary>
internal sealed class FakeSyncPolicy(
    CacheStrategy strategy = CacheStrategy.CacheFirst,
    bool shouldInvalidate = true) : ISyncPolicy
{
    public CacheStrategy GetStrategy(HttpRequestMessage request) => strategy;

    public bool ShouldInvalidateCacheOnWrite(HttpRequestMessage request) => shouldInvalidate;

    public RetryOptions GetRetryOptions(HttpRequestMessage request) =>
        new(MaxRetries: 3, InitialDelay: TimeSpan.FromSeconds(1), BackoffMultiplier: 2.0);
}
