# Issue 03 — Request/Response Envelope Model

## Summary

Define the `Envelope` document type that Hyperwyc uses to persist both outbound (queued write) and inbound (cached response) data in the store.

## Background

Every request/response pair Hyperwyc manages is stored as a single `Envelope` document. The same type covers both outbox entries (pending writes waiting for connectivity) and cache entries (GET responses stored for offline reads).

## Envelope Schema

```csharp
public sealed class Envelope
{
    public string Id { get; init; }           // GUID, also used as Idempotency-Key
    public string Url { get; init; }
    public string Method { get; init; }       // "GET", "POST", etc.
    public Dictionary<string, string> RequestHeaders { get; init; }
    public string? RequestBody { get; init; }

    public bool IsSynced { get; set; }
    public bool IsDeadLettered { get; set; }
    public int RetryCount { get; set; }
    public DateTimeOffset? NextRetryUtc { get; set; }
    public DateTimeOffset CreatedUtc { get; init; }

    public CachedResponse? Response { get; set; }
}

public sealed class CachedResponse
{
    public int StatusCode { get; init; }
    public Dictionary<string, string> Headers { get; init; }
    public string? Body { get; init; }
    public DateTimeOffset CachedAt { get; init; }
}
```

## Acceptance Criteria

- [x] `Envelope` and `CachedResponse` defined in `src/Hyperwyc`.
- [x] All properties have XML `<summary>` doc comments.
- [x] `Envelope` is serialisable to/from JSON without custom converters (use `System.Text.Json`-friendly property types).
- [x] A helper factory method `Envelope.ForRequest(HttpRequestMessage)` creates an unsent envelope from an `HttpRequestMessage`.
- [x] A helper `Envelope.ForCachedResponse(HttpRequestMessage, HttpResponseMessage)` creates a cache entry.
- [x] Unit tests cover the factory methods (round-trip serialisation, correct field mapping).

## Notes

- Body storage is string-based; binary bodies are out of scope for v0.1.
- Maximum body size enforcement is tracked in issue #17.
- `Id` is set at construction time, never externally assigned.
