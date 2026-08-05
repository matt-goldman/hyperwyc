namespace Hyperwyc.Models;

/// <summary>
/// Discriminates the kind of sync lifecycle event emitted on
/// <see cref="Interfaces.IHyperwyc.SyncEvents"/>.
/// </summary>
public enum SyncEventType
{
    /// <summary>A write request has been queued in the outbox.</summary>
    OnQueued,

    /// <summary>A queued request is being retried after a previous failure.</summary>
    OnRetrying,

    /// <summary>A queued request was successfully synced to the server.</summary>
    OnSynced,

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
}
