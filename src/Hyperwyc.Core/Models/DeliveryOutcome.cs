using System.Text;

namespace Hyperwyc.Models;

/// <summary>
/// What the server — or the network — said on a delivery attempt for a queued write.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Kind"/> alone says whether Hyperwyc is finished with the envelope.
/// <see cref="DeliveryOutcomeKind.Delivered"/> is final and the envelope is discarded with it;
/// <see cref="DeliveryOutcomeKind.TransportFailure"/> means it stays in the outbox for the next
/// flush.
/// </para>
/// <para>
/// <b>Only a transport failure is persisted.</b> It is written to <see cref="Envelope.LastOutcome"/>
/// because the envelope is still there to carry it, and it is the only account of why an outbox is
/// not draining — see
/// <see href="https://github.com/mattgoldman/hyperwyc/blob/main/Backlog/23-v1-diagnostics-view.md">issue 23</see>.
/// A delivery outcome is published on <see cref="HyperwycEvent.Outcome"/> and nowhere else: what the
/// server said is an ordinary HTTP response, and keeping it is the application's business rather
/// than Hyperwyc's. See
/// <see href="https://github.com/mattgoldman/hyperwyc/blob/main/docs/decisions/0010-retain-only-outstanding-work.md">ADR 0010</see>,
/// and note the consequence: <b>the event is the only report of a delivery</b>, so a consumer must
/// be subscribed before a flush runs.
/// </para>
/// <para>
/// Everything here still has to survive serialisation, which is why there is no
/// <see cref="Exception"/> — see <see cref="Error"/>.
/// </para>
/// </remarks>
public sealed record DeliveryOutcome
{
    /// <summary>What happened on the attempt.</summary>
    public DeliveryOutcomeKind Kind { get; init; }


    /// <summary>
    /// The HTTP status code, or <see langword="null"/> for a
    /// <see cref="DeliveryOutcomeKind.TransportFailure"/> where no response arrived.
    /// </summary>
    public int? StatusCode { get; init; }

    /// <summary>The reason phrase accompanying <see cref="StatusCode"/>, if the server sent one.</summary>
    public string? ReasonPhrase { get; init; }


    /// <summary>
    /// The response body, up to <see cref="HyperwycOptions.MaxOutcomeBodyBytes"/>.
    /// <see langword="null"/> when the response had no body or none could be read.
    /// </summary>
    /// <remarks>
    /// Bytes rather than a string because a body is not necessarily text, and because it is
    /// the shape the rest of the envelope moves to under
    /// <see href="https://github.com/mattgoldman/hyperwyc/blob/main/Backlog/25-binary-request-response-bodies.md">issue 25</see>.
    /// <see cref="GetBodyAsText"/> covers the common case.
    /// </remarks>
    public byte[]? Body { get; init; }

    /// <summary>
    /// Whether <see cref="Body"/> was cut short at the configured limit. A truncated error
    /// message is still worth having, so the body is clipped rather than discarded.
    /// </summary>
    public bool BodyTruncated { get; init; }

    /// <summary>
    /// The transport failure message, for <see cref="DeliveryOutcomeKind.TransportFailure"/>.
    /// <see langword="null"/> otherwise.
    /// </summary>
    /// <remarks>
    /// A string rather than the <see cref="Exception"/> itself, for a structural reason rather
    /// than a stylistic one: this record is persisted, and an exception does not round-trip
    /// through a document store. Handing one out on the event while the stored copy carried a
    /// string would mean two shapes for the same fact. The exception type is also an
    /// implementation detail of whichever transport is in use, which is not a detail worth
    /// making part of Hyperwyc's public contract.
    /// </remarks>
    public string? Error { get; init; }


    /// <summary>When the attempt completed, in UTC.</summary>
    public DateTimeOffset OccurredUtc { get; init; }

    /// <summary>
    /// <see cref="Body"/> decoded as UTF-8, or <see langword="null"/> if there is no body.
    /// </summary>
    /// <remarks>
    /// Assumes UTF-8 rather than consulting the response's charset. That covers essentially
    /// every JSON API, and a caller that needs something else has the raw <see cref="Body"/>.
    /// </remarks>
    public string? GetBodyAsText() => Body is null ? null : Encoding.UTF8.GetString(Body);
}
