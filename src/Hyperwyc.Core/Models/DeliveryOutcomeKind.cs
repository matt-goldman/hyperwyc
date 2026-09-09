namespace Hyperwyc.Models;

/// <summary>
/// What happened on a delivery attempt. <see cref="Delivered"/> is final; only
/// <see cref="TransportFailure"/> leaves the envelope in the outbox.
/// </summary>
public enum DeliveryOutcomeKind
{
    /// <summary>
    /// The server answered. What it answered is in <see cref="DeliveryOutcome.StatusCode"/> and
    /// <see cref="DeliveryOutcome.Body"/>, and it makes no difference to Hyperwyc: any answer
    /// means the request reached the API, so the delivery is complete and the envelope is gone.
    /// Whether the answer is good news is the application's judgement, on information Hyperwyc
    /// does not have. See
    /// <see href="https://github.com/mattgoldman/hyperwyc/blob/main/docs/decisions/0010-delivery-ends-hyperwycs-interest.md">ADR 0010</see>.
    /// </summary>
    Delivered,

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
