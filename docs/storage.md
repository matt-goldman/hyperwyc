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


## Implementing your own

Install `Hyperwyc.Core` instead of `Hyperwyc`, implement `IHyperwycStore`, and register it as a type parameter:

```csharp
services.AddHyperwycCore<MyStore>();          // the container constructs it
services.AddHyperwycCore(sp => new MyStore(...));   // or supply a factory
```

It is a type parameter rather than an option so that forgetting it is a compile error rather than a silent fall back to in-memory storage that loses everything on restart.

The interface is seven methods and most of them are obvious. What follows is the part that is not.

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

Three methods are the cache's, three are the outbox's, and `ResetAsync` clears both. Nothing crosses: a queued write is never returned by a cache method and a cached response is never returned by an outbox one.

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
| `ResetAsync`                    | Must work **when the store cannot be read** — clear the underlying files rather than enumerating records, because enumerating means deserialising, which is the thing that just failed                                                    |

### Throwing is how you report failure

Hyperwyc treats **any exception that is not `OperationCanceledException`** as the store being unusable. It then logs once, publishes `OnStoreUnreadable`, and passes every request through for the rest of the session.

So: let cancellation propagate untouched, and throw for genuine failures rather than returning `null` or an empty list. Swallowing an error and returning nothing tells Hyperwyc the store is healthy and simply empty, and it will carry on writing into something that cannot hold anything.

### Test it against both implementations

If you take one thing from this section: the defect above existed because `InMemoryStore` serialised everything, `CabinetStore` serialised nothing, and `IHyperwycStore` required neither. Every test passed against the safe one. Write your tests against the interface and run them against `InMemoryStore` as well as your own — anything that passes for one and not the other is a contract you have found rather than a bug you have written.

## When the store can't be read

A local store can become unreadable, e.g. a wrong encryption key, a directory that moved, files damaged. Hyperwyc's response is to **report it and get out of the way**:

- it logs, through an `ILogger` if your IoC container has one;
- it publishes `OnStoreUnreadable` once (not once per request);
- and every request from then on passes straight through, as though Hyperwyc weren't installed.

Nothing is deleted and nothing is thrown. A damaged store is still your data, and deleting it is irreversible; refusing to start is a decision your application might reasonably make but Hyperwyc has no standing to make for you.

**Offline writes are declined rather than accepted.** With no store to hold them, a `202` would promise delivery Hyperwyc cannot keep, so the request goes to the transport and fails as it would without Hyperwyc there. That failure is visible and recoverable; a lost `202` is neither.

The simplest approach is to just reset the store. `IHyperwyc` provides a method for this:

```csharp
await hyperwyc.ResetStoreAsync();   // discards the store; caching and queueing resume
```

That works even when the store cannot be read — it clears the files rather than enumerating records, which would need to decrypt them first.

There is deliberately no option to do this for you. Discarding queued writes is the failure Hyperwyc exists to prevent, and the usual cause of an unreadable store is a changed key or path rather than corruption — so the bytes are generally intact, and destroying them on the first failed read forecloses recovery. Whether Hyperwyc should instead move a bad store aside and start a clean one, keeping both, is an open question rather than a settled no.

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

**The cache is not bounded.** Individual response bodies are capped at 512 KB, but the store as a whole grows without limit and nothing evicts. On a long-lived mobile app that is the number to watch — and it is the same problem as the 25 MB Android quota above, seen from the other end rather than a separate concern.

Cached entries are removed when a write invalidates their URL prefix, so the cache does shrink; what it has no answer for is an entry that is simply never invalidated and never re-read.

That is known and tracked rather than a surprise, and it cannot be worked around from outside the library.

