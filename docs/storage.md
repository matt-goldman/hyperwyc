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

TODO: Should we add an automatic call to this to HyperwycOptions? I.e., options.ResetStoreOnFailure = true or something?

## Excluding the store from OS backup

On iOS and Android the store sits in a location the operating system backs up to iCloud or Google Drive by default. You should exclude it from backups for an important reason.

**The outbox is a list of writes that have not happened yet.** If this is captured in a backup, the first time the app runs after a restore, if it has connectivity (or after connectivity is restored), those writes will be sent. Restore a three-week-old backup and Hyperwyc will faithfully replay a sale that was delivered a fortnight ago. Restore onto a second device and both devices hold the same pending writes, and both will send them. Hyperwyc has no duplicate suppression by design, so nothing catches it. Deduplication is not Hyperwyc's responsibility, and handling duplicate requests is something your API should already handle. A worse scenario is a non-duplicate request that no longer makes sense, especially if your API attaches a time received timestamp. But excluding the Hyperwyc store from backup is free and easy so you should usually just do it anyway. 

Both platforms support path-level exclusion, so the rest of your app's data can still be backed up as usual (and you can just opt-out the Hyperwyc store, rather than having to opt-in everything else).

- **iOS** — set `NSURLIsExcludedFromBackupKey` on the store directory once at startup. Apple's data-storage guidelines expect this for regenerable data.
- **Android** — an `<exclude domain="file" path="…"/>` entry in `data_extraction_rules` (API 31+) or `full_backup_content` below that. The `<cloud-backup>` and `<device-transfer>` split is worth using: a direct device-to-device transfer, where the old handset is being retired, does not carry the duplicate-replay risk that a cloud restore does.

TODO: include links for these

`CabinetStoreOptions.DefaultDirectoryPath()` gives you the path to exclude.

A secondary reason on Android: Auto Backup caps an app at 25 MB, and exceeding it silently stops backup for **the whole app** rather than just the offending files.
