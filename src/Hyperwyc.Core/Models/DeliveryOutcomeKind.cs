namespace Hyperwyc.Models;

/// <summary>
/// What happened on a delivery attempt. <see cref="Succeeded"/> and <see cref="Rejected"/> are
/// final; only <see cref="TransportFailure"/> leaves the envelope in the outbox.
/// </summary>
public enum DeliveryOutcomeKind
{
    /// <summary>The server accepted the request and answered with a success status.</summary>
    Succeeded,

    /// <summary>
    /// The server answered with a non-success status. Any answer means the request reached the
    /// API, so Hyperwyc's work is done and the outcome is final — whether to try again is the
    /// application's decision, on information Hyperwyc does not have.
    /// </summary>
    Rejected,

    /// <summary>
    /// No response was received — the connection failed, DNS did not resolve, or a captive
    /// portal intercepted the request.
    /// </summary>
    /// <remarks>
    /// The network being unusable says nothing about the request, so the flush stops and the
    /// envelope waits for the next connectivity signal.
    /// See <see href="https://github.com/mattgoldman/hyperwyc/blob/main/Backlog/Done/38-retry-classification.md">issue 38</see>.
    /// </remarks>
    TransportFailure,
}
