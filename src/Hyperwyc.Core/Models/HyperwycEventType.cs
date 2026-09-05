namespace Hyperwyc.Models;

/// <summary>
/// Discriminates the kind of sync lifecycle event emitted on
/// <see cref="Interfaces.IHyperwyc.Events"/>.
/// </summary>
public enum HyperwycEventType
{
    /// <summary>A write request has been queued in the outbox.</summary>
    OnQueued,


    /// <summary>A queued request was successfully synced to the server.</summary>
    OnDelivered,

    /// <summary>
    /// A queued request failed permanently and has been moved to the dead-letter
    /// queue.
    /// </summary>
    OnFailed,

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
