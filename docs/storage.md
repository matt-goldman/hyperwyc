# Storage

Where Hyperwyc keeps cached responses and queued writes, how they are protected, and what happens when the store cannot be read.

By default the store lives in a `Hyperwyc` folder under `LocalApplicationData` — inside the app sandbox on Android and iOS — and is encrypted with AES-256-GCM using a key derived from that path.

That default costs you nothing and keeps cached data from casual inspection of the device filesystem, but the derived key is deterministic, so it is not a defence against an attacker who has the device and knows what this library does. If the cached data warrants more, supply your own key:

```csharp
services.AddHyperwyc(configureStore: store =>
{
    store.DirectoryPath = myPath;
    store.EncryptionKey = keyFromSecureStorage;   // 32 bytes
});
```

Losing that key means losing access to everything already stored.

The store serialises through a source-generated `JsonSerializerContext`, so it is safe under trimming and AOT and needs nothing from you to be — see [Hyperwyc in a .NET MAUI app](maui.md#5-aot-and-trimming).

Request and response bodies are held as separate encrypted files rather than inside the record, and are read only on the path that needs them. That matters because the record document is rewritten in full on every write: a body left inline would be re-encrypted and rewritten every time any other entry changed.

Cached responses and queued writes are kept in separate documents, so a burst of cache writes does not rewrite the outbox and a flush does not rewrite the cache.


## What ends up on disk

Hyperwyc stores the request the application made, **including every header that was on it when Hyperwyc saw it** — `Authorization` and any API key among them. There is no deny-list and no redaction.

That is deliberate rather than an oversight. A replay has to reproduce the request, and a credential that is still valid at replay time is part of the request: an API key, basic auth, or an HMAC over stable request content all still work a day later, so dropping them would turn a replay that would have succeeded into one that cannot. Deciding which headers are "sensitive" is a judgement about your threat model, and Hyperwyc does not know it — the same reasoning as [ADR 0001](decisions/0001-idempotency-is-not-hyperwycs-remit.md), which declined to make that kind of decision on your behalf over idempotency.

**How long it stays is not the same for the two record kinds.**

| | What it keeps | For how long |
| --- | --- | --- |
| A queued write | The headers of the request you made | Only while the write is outstanding. A delivered write is deleted outright, headers with it — a delivery being any answer from the server, refusal included ([ADR 0010](decisions/0010-delivery-ends-hyperwycs-interest.md)) |
| A cached response | The headers of the `GET` that populated it | The life of the entry — which nothing bounds, since nothing evicts. It goes when a write invalidates its URL prefix, or when the store is reset |

So the cache is the one to think about. A queued write's exposure is measured against how long you are offline; a cached entry's is not measured against anything.

### The two levers

**Add credentials in a handler registered after `AddHyperwycHandler()`.** Then they are never captured at all — Hyperwyc has already serialised the envelope by the time that handler runs — and a fresh one is minted at replay time. This is [the ordering already recommended](pipeline.md); keeping credentials out of the store is its second reason. Cookies are never captured either, for the same reason: the primary handler's `CookieContainer` attaches them below Hyperwyc.

**Supply your own encryption key** for what does get stored, rather than relying on the path-derived default described above. That is the whole of the mitigation for a credential a caller sets directly on the request, which is the one case the ordering cannot help with.

There is no option to exclude headers from what is persisted. If one is ever added it will be opt-in — you naming the headers you want dropped, and accepting what that does to replay — rather than Hyperwyc choosing for you.


## Implementing your own

Install `Hyperwyc.Core` instead of `Hyperwyc`, implement `IHyperwycStore`, and register it as a type parameter:

```csharp
services.AddHyperwycCore<MyStore>();          // the container constructs it
services.AddHyperwycCore(sp => new MyStore(...));   // or supply a factory
```

It is a type parameter rather than an option so that forgetting it is a compile error rather than a silent fall back to in-memory storage that loses everything on restart.

The interface is eight methods, one of which has a default implementation, and most of them are obvious. What follows is the part that is not.

### It must be safe for concurrent use

**This is a requirement, not a nicety, and it is the one that will bite you.**

`HyperwycHandler` is registered transient and runs on whatever thread its caller used, so two overlapping HTTP requests reach your store at the same time — and a flush runs on a background task alongside them. Every mutating method is a read-modify-write, so a store that serialises nothing will interleave them.

Hyperwyc's own Cabinet-backed store originally shipped without this and corrupted its file under two concurrent requests: two overlapping saves raced a write-temp-then-move, and the second move threw on a file that was no longer there. It had never been synchronised, and every store test had been running against the in-memory implementation, which was.

Both shipped implementations now serialise every operation behind a single `SemaphoreSlim`. That is the simple answer and it is fast enough. Finer-grained locking is ok if you need it, but less will guarantee problems.

### Two kinds of record, two types

Hyperwyc stores two things and they have nothing in common but a URL:

|                            | Keyed on         | Holds                                                             |
| -------------------------- | ---------------- | ----------------------------------------------------------------- |
| `QueuedWrite`              | a generated `Id` | the captured request — method, headers, body, client name — and the outcome of the last delivery attempt |
| `CachedResponse`           | its `Url`        | the stored response — status, headers, body — and when it was cached |

Three methods are the cache's, three are the outbox's, and `ResetAsync` and `TryQuarantineAsync` are about the store as a whole. Nothing crosses: a queued write is never returned by a cache method and a cached response is never returned by an outbox one.

These were one `Envelope` type until [issue 55](https://github.com/mattgoldman/hyperwyc/blob/main/Backlog/Done/55-envelope-kind-discriminator.md), told apart by an `IsSynced` boolean set `true` on responses that had been synced nowhere. If you wrote a store against that shape, the port is mechanical — the flag becomes the choice of which method is being called — and your queries get simpler, because every "is this the other kind?" filter goes.

A cached response is keyed by the URL it caches, which is what stops the cache accumulating one entry per fetch: a second fetch of the same URL replaces the first. **Do not key it on anything else.**

### What each method owes

| Method                          | The part that is not obvious                                                                                                                                                                                                              |
| ------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `GetCachedResponseAsync`        | Match the URL exactly. TTL is not your concern — the handler decides staleness                                                                                                |
| `PutCachedResponseAsync`        | Keyed on `CachedResponse.Url`. Insert or replace; never append                                                                                                                                                                            |
| `InvalidateCacheForPrefixAsync` | **Removes the record**, rather than emptying it. An ordinal `StartsWith` on the URL. An entry left with no body is reachable by nothing and nothing will ever come back for it                                                             |
| `GetPendingOutboxAsync`         | Return every queued write, **ordered by `CreatedUtc` ascending**. The ordering is yours to provide — the processor does not sort, and delivery order is a promise Hyperwyc makes to its callers |
| `UpsertQueuedWriteAsync`        | Keyed on `QueuedWrite.Id`. Both an insert and a replace are real — the handler queues, and the processor writes back a failed attempt's outcome                                                                                            |
| `RemoveDeliveredAsync`          | **Delete the record**, do not flag it — request body and headers included. Hyperwyc is finished with a write once it has been delivered ([ADR 0010](decisions/0010-delivery-ends-hyperwycs-interest.md)), and a delivery is any answer from the server, refusal included. Takes an id and must tolerate one it does not recognise |
| `ResetAsync`                    | Must work **when the store cannot be read** — clear the underlying files rather than enumerating records, because enumerating means deserialising, which is the thing that just failed. Takes any quarantine with it: reset means discard |
| `TryQuarantineAsync`            | Optional — the default returns `false`, which gets you the report-and-step-aside behaviour below. Implement it if you have somewhere to put an unreadable store. **Move, do not delete**, and **return `false` if you already have one**: that is the whole bound on how much can pile up |

### Throwing is how you report failure

Hyperwyc treats **any exception that is not `OperationCanceledException`** as the store being unusable. It then logs once, publishes `OnStoreUnreadable`, and passes every request through for the rest of the session.

So: let cancellation propagate untouched, and throw for genuine failures rather than returning `null` or an empty list. Swallowing an error and returning nothing tells Hyperwyc the store is healthy and simply empty, and it will carry on writing into something that cannot hold anything.

### Test it against both implementations

If you take one thing from this section: the defect above existed because `InMemoryStore` serialised everything, `CabinetStore` serialised nothing, and `IHyperwycStore` required neither. Every test passed against the safe one. Write your tests against the interface and run them against `InMemoryStore` as well as your own — anything that passes for one and not the other is a contract you have found rather than a bug you have written.

## When the store can't be read

A local store can become unreadable, e.g. a wrong encryption key, a directory that moved, files damaged. Hyperwyc **moves it aside once and starts a clean one**, so that caching and queueing carry on:

- it logs, through an `ILogger` if your IoC container has one;
- it renames the store's contents into a `quarantine` directory inside the store directory — nothing is deleted;
- it publishes `OnStoreQuarantined` once (not once per request);
- and every request from the next one onward behaves as though the device had simply never held a store.

There is no setting for this and no way to turn it off. A setting would not help: a default nobody changes is the behaviour that ships, so the question is only which behaviour is right.

**Moved, not deleted.** The usual cause is a changed key or path rather than corruption, so the bytes are generally intact and merely unopenable, and they are your users' queued writes. Destroying them on a failed read would foreclose a recovery that is often still possible.

**The request that discovered the problem still degrades.** A read falls through to the network and a write is declined rather than accepted — with no store to hold it, a `202` would promise delivery Hyperwyc cannot keep, so the request goes to the transport and fails as it would without Hyperwyc there. That failure is visible and recoverable; a lost `202` is neither. The *next* request is served from the clean store as normal.

**It happens at most once per session.** If the new store also becomes unreadable, Hyperwyc does not set that one aside as well. It publishes `OnStoreUnreadable`, and every request from then on passes straight through as though Hyperwyc weren't installed.

A store that breaks twice is a systemic fault rather than an incident, and churning stores through it would mask the underlying issue. It also bounds what piles up on the device, at 2× and with no sweep, no scheduler and nothing to configure, as the existence of the quarantine directory is the counter. On Android that matters for the 25 MB Auto Backup quota; the quarantine sits inside the store directory, so the one path you already exclude from backup covers both.

You can still discard everything explicitly, which is the way back to a clean slate after a second failure:

```csharp
await hyperwyc.ResetStoreAsync();   // discards the store and any quarantine; caching and queueing resume
```

That works even when the store cannot be read; it clears the files rather than enumerating records, which would need to decrypt them first. It deletes the quarantine as well as the primary store, because reset means discard: the case it exists for is logout, and leaving the previous user's queued writes on disk is the opposite of what was asked. Resetting also re-arms the quarantine, so the next failure is treated as a first one again.

### Getting at a quarantined store

Assuming you are using the default implementation, the quarantined store is an ordinary Cabinet store, and `CabinetStore.OpenQuarantined` returns it (or `null` if there isn't one):

```csharp
var quarantined = CabinetStore.OpenQuarantined(storeOptions);
if (quarantined is not null)
{
    foreach (var write in await quarantined.GetPendingOutboxAsync())
    {
        // Whatever you want to do with it. Hyperwyc will not replay these itself: they were
        // queued under a 202 that was answered long ago, and re-sending them is a decision
        // about your API rather than about storage.
    }
}
```

**Use that rather than opening the directory yourself.** With no explicit `EncryptionKey` the key is derived from the store's own path, and the quarantined bytes were written under the key derived from the *original* path — so opening `…/Hyperwyc/quarantine` as an ordinary store derives a different key and finds it unreadable, which looks exactly like the damage that caused the quarantine in the first place. `OpenQuarantined` carries the derivation across the move; pass it the same `CabinetStoreOptions` the live store was configured with.

`CabinetStore.QuarantinePath(storeOptions)` gives you the directory, if you want to delete it once you have what you need. Until it is gone, a second failure cannot be quarantined.

## Excluding the store from OS backup

On iOS and Android the store sits in a location the operating system backs up to iCloud or Google Drive by default. You should exclude it from backups for an important reason.

**A restored backup re-sends writes that already happened.**

The outbox is a list of writes that have not gone out yet, so anything captured in a backup is replayed the first time the app runs after a restore. Restore a three-week-old backup and Hyperwyc faithfully re-sends a sale delivered a fortnight ago. Restore onto a second device and both hold the same pending writes, and both send them. Hyperwyc has no duplicate suppression by design, so nothing catches it.

Your API should already tolerate duplicates, and deduplication is not Hyperwyc's job. The worse case is the write that is not a duplicate but no longer makes sense — an order for a basket that has changed, or anything your API timestamps on receipt.

Excluding the store from backup is free, so do it anyway.

Both platforms support path-level exclusion, so the rest of your app's data can still be backed up as usual (and you can just opt-out the Hyperwyc store, rather than having to opt-in everything else).

- **iOS** — set `NSURLIsExcludedFromBackupKey` on the store directory once at startup. Apple's data-storage guidelines expect this for regenerable data.
- **Android** — an `<exclude domain="file" path="…"/>` entry in `data_extraction_rules` (API 31+) or `full_backup_content` below that. The `<cloud-backup>` and `<device-transfer>` split is worth using: a direct device-to-device transfer, where the old handset is being retired, does not carry the duplicate-replay risk that a cloud restore does.

Apple's [iOS Data Storage Guidelines](https://developer.apple.com/icloud/documentation/data-storage/) are the justification for the iOS case, and [`NSURLIsExcludedFromBackupKey`](https://developer.apple.com/documentation/foundation/nsurlisexcludedfrombackupkey) is the key itself. For Android, [Back up user data with Auto Backup](https://developer.android.com/guide/topics/data/autobackup) covers both the quota and the `data_extraction_rules` format.
`CabinetStoreOptions.DefaultDirectoryPath()` gives you the path to exclude.

A secondary reason on Android: Auto Backup caps an app at 25 MB, and exceeding it silently stops backup for **the whole app** rather than just the offending files.

## One thing to know before you ship

**The cache is not bounded.** Individual response bodies are capped — `HyperwycOptions.MaxCachedResponseBodyBytes`, 512 KB by default, and settable [per route](delivery.md#how-big-a-response-may-be) — but the store as a whole grows without limit and nothing evicts. On a long-lived mobile app that is the number to watch — and it is the same problem as the 25 MB Android quota above, seen from the other end rather than a separate concern.

Which is why the per-route cap is the one to reach for. Raising the global cap to fit one endpoint's payloads raises it for every endpoint, and on a store nothing evicts from, that headroom is what eventually fills the disk.

Cached entries are removed when a write invalidates their URL prefix, so the cache does shrink; what it has no answer for is an entry that is simply never invalidated and never re-read.

That is known and tracked rather than a surprise, and it cannot be worked around from outside the library.

