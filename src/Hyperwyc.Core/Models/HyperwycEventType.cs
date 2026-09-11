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
    /// Hyperwyc's store could not be read, so it was moved aside and a clean one started.
    /// Caching and queueing continue.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Published at most once per session — a store that becomes unreadable a second time is not
    /// set aside again, and raises <see cref="OnStoreUnreadable"/> instead. Like that event it is
    /// not about a single HTTP request, so <c>Url</c> and <c>Method</c> are empty.
    /// </para>
    /// <para>
    /// Nothing was lost that was ever going to be recovered, but something was lost: anything
    /// queued in the old store is no longer going to be delivered, and anything cached in it is
    /// no longer available offline. An application that shows sync state should say so. The
    /// bytes themselves are still on disk — see
    /// <see href="https://github.com/mattgoldman/hyperwyc/blob/main/docs/storage.md">storage.md</see>
    /// for how to reach them.
    /// </para>
    /// </remarks>
    OnStoreQuarantined,

    /// <summary>
    /// Hyperwyc's store could not be read and could not be set aside, so caching and queueing are
    /// disabled for the rest of the session and requests pass straight through.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Published once, not per request. The only event that is not about a single HTTP request,
    /// so <c>Url</c> and <c>Method</c> are empty.
    /// </para>
    /// <para>
    /// Reached either because the store has nowhere to set its contents aside, or because it has
    /// already been set aside once this session — see <see cref="OnStoreQuarantined"/>. A second
    /// failure is a systemic fault rather than an incident, and churning another copy onto the
    /// device would hide the thing someone needs to see.
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
