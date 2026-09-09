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

### One type, two kinds of record

`Envelope` is both a **queued write** and a **cached response**, told apart by flags rather than by type:

|                 | `IsSynced`               | `Response`          | `Id`                         |
| --------------- | ------------------------ | ------------------- | ---------------------------- |
| Queued write    | `false`                  | `null`              | a generated id               |
| Cached response | **`true` from creation** | the stored response | `cache:{url}`, deterministic |

A cached response is marked synced because it is *not a pending write* — it was never "synced" anywhere. Nothing ever flips the flag: a delivered write is removed from the store rather than marked, so in practice this says nothing but *which kind of record this is*. The name is wrong and is being dealt with; what matters for you is that **the outbox is defined by the negation of that flag**, so your `GetPendingOutboxAsync` must filter on it.

The deterministic `cache:{url}` id is what stops cached responses accumulating one per fetch — a second fetch of the same URL upserts over the first. Do not key on anything else.

### What each method owes

| Method                          | The part that is not obvious                                                                                                                                                                                                              |
| ------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `GetPendingOutboxAsync`         | Return **only** envelopes that are not synced, **ordered by `CreatedUtc` ascending**. The ordering is yours to provide — the processor does not sort, and delivery order is a promise Hyperwyc makes to its callers |
| `GetCachedResponseAsync`        | Match the URL exactly, and return only an envelope that actually has a `Response`. TTL is not your concern — the handler decides staleness                                                                                                |
| `UpsertAsync`                   | Keyed on `Envelope.Id`. Insert or replace; never append                                                                                                                                                                                   |
| `RemoveDeliveredAsync`          | **Delete the record**, do not flag it — request body and headers included. Hyperwyc holds outstanding work and nothing else ([ADR 0010](decisions/0010-retain-only-outstanding-work.md)), and a delivery is any answer from the server, a `409` as much as a `201`. Takes an id and must tolerate one it does not recognise |
| `InvalidateCacheForPrefixAsync` | **Removes the record**, and only records that actually have a `Response`. An ordinal `StartsWith` on the URL. The filter matters twice: a queued write under the same prefix must not be touched, and an entry with no response is not yours to reclaim here |
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

