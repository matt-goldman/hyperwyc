namespace Hyperwyc.Models;

/// <summary>
/// What happened on a delivery attempt, as distinct from what Hyperwyc intends to do next
/// (<see cref="SyncOutcome.IsFinal"/>).
/// </summary>
public enum SyncOutcomeKind
{
    /// <summary>The server accepted the request and answered with a success status.</summary>
    Succeeded,

    /// <summary>
    /// The server rejected the request outright — a 4xx other than 408 or 429. Replaying it
    /// unchanged produces the same answer, so it is never retried.
    /// </summary>
    Rejected,

    /// <summary>
    /// The server answered, but with something worth trying again: a 5xx, a 408, or a 429.
    /// Consumes one attempt from the retry budget.
    /// </summary>
    TransientFailure,

    /// <summary>
    /// No response was received — the connection failed, DNS did not resolve, or a captive
    /// portal intercepted the request.
    /// </summary>
    /// <remarks>
    /// Does not consume the retry budget. The network being unusable says nothing about the
    /// request, so the flush stops and the envelope waits for the next connectivity signal.
    /// See <see href="https://github.com/mattgoldman/hyperwyc/blob/main/Backlog/Done/38-retry-classification.md">issue 38</see>.
    /// </remarks>
    TransportFailure,
}
