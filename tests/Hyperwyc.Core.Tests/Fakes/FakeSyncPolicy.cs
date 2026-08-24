using Hyperwyc.Interfaces;
using Hyperwyc.Models;

namespace Hyperwyc.Tests.Fakes;

/// <summary>
/// Test double for <see cref="ISyncPolicy"/> with configurable behaviour.
/// </summary>
internal sealed class FakeSyncPolicy(
    CacheStrategy strategy = CacheStrategy.CacheFirst,
    bool shouldInvalidate = true) : ISyncPolicy
{
    public CacheStrategy GetStrategy(HttpRequestMessage request) => strategy;

    public bool ShouldInvalidateCacheOnWrite(HttpRequestMessage request) => shouldInvalidate;
}
