using Restyc.Interfaces;
using Restyc.Models;

namespace Restyc.Tests.Fakes;

/// <summary>
/// Test double for <see cref="IStalenessEvaluator"/>.
/// Returns a fixed staleness value for every envelope.
/// </summary>
internal sealed class FakeStalenessEvaluator(bool isStale = false) : IStalenessEvaluator
{
    public bool IsStale(Envelope cachedEnvelope, DateTimeOffset now) => isStale;
}
