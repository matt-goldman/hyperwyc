using System.Text.Json.Serialization;
using Hyperwyc.Models;

namespace Hyperwyc.Cabinet;

/// <summary>
/// Source-generated serialisation metadata for everything <see cref="CabinetStore"/> persists.
/// </summary>
/// <remarks>
/// <para>
/// Cabinet's <c>RecordSet&lt;T&gt;</c> saves the whole set as a single document, so the only
/// types that cross the serialiser are <see cref="List{T}"/> of <see cref="CachedResponse"/> and
/// of <see cref="QueuedWrite"/> — one set per kind of record. Declaring the element types as well
/// is not required — the generator walks the graph — but it is what makes the generated metadata
/// for each addressable, and it says out loud what the store's persisted shape is.
/// </para>
/// <para>
/// This has to live in Hyperwyc rather than in Cabinet: System.Text.Json's generator cannot see
/// a context emitted by another generator in the same compilation, so Cabinet deliberately does
/// not produce one. It is Hyperwyc's model being persisted, through a store Hyperwyc constructs
/// internally, so it is also not something a consumer could supply from outside.
/// </para>
/// <para>
/// Internal on purpose: the context is an implementation detail of <see cref="CabinetStore"/>,
/// and nothing outside this assembly has a reason to hold one. Referencing public model types
/// from an internal context is legal; the accessibility rule only bites the other way round.
/// </para>
/// </remarks>
[JsonSerializable(typeof(List<CachedResponse>))]
[JsonSerializable(typeof(CachedResponse))]
[JsonSerializable(typeof(List<QueuedWrite>))]
[JsonSerializable(typeof(QueuedWrite))]
[JsonSourceGenerationOptions(WriteIndented = false)]
internal sealed partial class HyperwycJsonContext : JsonSerializerContext
{
}
