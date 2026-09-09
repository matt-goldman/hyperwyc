using System.Net.Http.Headers;

namespace Hyperwyc.Models;

/// <summary>
/// Flattens <see cref="HttpHeaders"/> into the dictionary shape both stored kinds persist.
/// </summary>
/// <remarks>
/// Shared by <see cref="QueuedWrite"/> and <see cref="CachedResponse"/> because the two kinds
/// capture headers identically and only differ in whose they are. Case-insensitive, because
/// HTTP header names are.
/// </remarks>
internal static class HeaderMap
{
    public static Dictionary<string, string> Flatten(HttpHeaders headers) =>
        headers.ToDictionary(
            h => h.Key,
            h => string.Join(", ", h.Value),
            StringComparer.OrdinalIgnoreCase);
}
