namespace Hyperwyc.Tests;

/// <summary>
/// A fresh <see cref="StoreHealth"/> for a test that does not care about it.
/// </summary>
/// <remarks>
/// In production this is a shared singleton, because the handler is transient and the processor
/// is not. A test constructing one handler has nobody to share with, so a private instance is
/// correct; tests that <em>do</em> care construct one explicitly and hold onto it.
/// </remarks>
internal static class TestHealthFactory
{
    public static StoreHealth TestHealth() => new(new HyperwycEventStream());
}
