namespace Hyperwyc.Models;

/// <summary>
/// Discriminates the kind of sync lifecycle event emitted on
/// <see cref="Interfaces.IHyperwyc.Events"/>.
/// </summary>
public enum HyperwycEventType
{
    /// <summary>A write request has been queued in the outbox.</summary>
    OnQueued,


    /// <summary>
    /// A queued request reached the server, which answered.
    /// </summary>
    /// <remarks>
    /// Raised whatever the status. An acceptance and a refusal are the same event from
    /// Hyperwyc's side — the request reached the API and the API answered — so what it said is
    /// carried on <see cref="HyperwycEvent.Outcome"/> for the application to judge. The envelope
    /// is gone by the time this is published, and nothing about it is retained, which makes this
    /// event the <b>only</b> report of what happened: see
    /// <see href="https://github.com/mattgoldman/hyperwyc/blob/main/docs/decisions/0010-delivery-ends-hyperwycs-interest.md">ADR 0010</see>.
    /// </remarks>
    OnDelivered,

    /// <summary>
    /// A cached GET response has been updated with a fresh response from the
    /// server.
    /// </summary>
    OnUpdated,

    /// <summary>
    /// Hyperwyc's store could not be read, so caching and queueing are disabled for the rest of
    /// the session and requests pass straight through.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Published once, not per request. The only event that is not about a single HTTP request,
    /// so <c>Url</c> and <c>Method</c> are empty.
    /// </para>
    /// <para>
    /// Carries no detail of the failure on purpose: the event is a signal an application can act
    /// on — offer a reset, warn that local data is unavailable — while the diagnosis goes to the
    /// log, where whoever needs the exception is already looking. The remedy is
    /// <see cref="Interfaces.IHyperwyc.ResetStoreAsync"/>, and it is the application's to call.
    /// </para>
    /// </remarks>
    OnStoreUnreadable,
}
