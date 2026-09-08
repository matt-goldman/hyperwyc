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

[comment: I would say no, and record it as a deliberate absence rather than build the knob.

The outbox is a list of writes that have not happened yet. ResetStoreOnFailure = true means "if the store cannot be read, silently discard writes the caller was told had been accepted" - which is defaults test question 3, quietly producing the failure the library exists to prevent. It is also wrong-and-silent by construction: the consumer finds out by the data not being there.

And it would fire in exactly the case where the data is most likely intact. The usual cause of an unreadable store is a key or a path change, not corruption - the bytes are fine and merely unreadable, and the fix is often to restore the key rather than to destroy the store. Deleting on the first failed read forecloses that.

The current design is the right shape: report it, get out of the way, and give the application a method to call once it has decided. That decision needs things Hyperwyc does not have - whether the app keeps its own record of the pending writes, whether the user should be told, whether this is a fresh install or a restore onto a new device.

If the real complaint is ergonomics, the smaller answer is a documented one-liner rather than an option: subscribe to OnStoreUnreadable and call ResetStoreAsync from the handler. Same behaviour, opted into explicitly, living in the consumer's code where the decision belongs - and it is three lines to show on this page. Filed as under-consideration with this reasoning.]

## Excluding the store from OS backup

On iOS and Android the store sits in a location the operating system backs up to iCloud or Google Drive by default. You should exclude it from backups for an important reason.

**The outbox is a list of writes that have not happened yet.** If this is captured in a backup, the first time the app runs after a restore, if it has connectivity (or after connectivity is restored), those writes will be sent. Restore a three-week-old backup and Hyperwyc will faithfully replay a sale that was delivered a fortnight ago. Restore onto a second device and both devices hold the same pending writes, and both will send them. Hyperwyc has no duplicate suppression by design, so nothing catches it. Deduplication is not Hyperwyc's responsibility, and handling duplicate requests is something your API should already handle. A worse scenario is a non-duplicate request that no longer makes sense, especially if your API attaches a time received timestamp. But excluding the Hyperwyc store from backup is free and easy so you should usually just do it anyway. 

[comment: This paragraph carries four separate points - the replay hazard, the two-device case, that deduplication is not Hyperwyc's job, and the stale-request scenario - and lands on "you should usually just do it anyway". It is the strongest safety argument in the docs and it currently reads as an aside. Leading with the one-sentence version would fix it: a restored backup re-sends writes that already happened. Everything else is support for that sentence. (Trailing whitespace on the line, too.)]

Both platforms support path-level exclusion, so the rest of your app's data can still be backed up as usual (and you can just opt-out the Hyperwyc store, rather than having to opt-in everything else).

- **iOS** — set `NSURLIsExcludedFromBackupKey` on the store directory once at startup. Apple's data-storage guidelines expect this for regenerable data.
- **Android** — an `<exclude domain="file" path="…"/>` entry in `data_extraction_rules` (API 31+) or `full_backup_content` below that. The `<cloud-backup>` and `<device-transfer>` split is worth using: a direct device-to-device transfer, where the old handset is being retired, does not carry the duplicate-replay risk that a cloud restore does.

[comment: Backlog item 48 is still marked Open and its whole scope is "documentation" - which this section now is. Worth closing it against this section, or narrowing it to whatever is genuinely left (the concrete snippets, probably).]

TODO: include links for these

[comment: The two you want are:

  - iOS: NSURLIsExcludedFromBackupKey, https://developer.apple.com/documentation/foundation/nsurlisexcludedfrombackupkey - and Apple's iOS Data Storage Guidelines for the "expects this for regenerable data" claim, which is the part worth citing since it is the justification rather than the API.
  - Android: https://developer.android.com/guide/topics/data/autobackup for the 25 MB quota and the opt-out model, and the data-extraction-rules reference on the same page for the cloud-backup / device-transfer split.

Worth opening both before committing them - Apple in particular moves documentation paths without redirects.]

`CabinetStoreOptions.DefaultDirectoryPath()` gives you the path to exclude.

A secondary reason on Android: Auto Backup caps an app at 25 MB, and exceeding it silently stops backup for **the whole app** rather than just the offending files.

[comment: Two things this page does not say that a consumer shipping the library needs.

The cache has no eviction. Individual response bodies are capped at 512 KB, but the store as a whole grows without bound and nothing removes anything (backlog item 42). On a long-lived mobile app that is the number that matters. It is also the same problem as the 25 MB quota above, seen from the other end - the quota is not a separate concern, it is the first consequence of the unbounded growth, and saying so joins them up.

Serialisation is reflection-based; there is no JsonSerializerContext (item 53). This is the page where AOT actually bites, and it bites the iOS audience specifically, since release builds have it on by default.

Both are open backlog items rather than defects, which is exactly why they belong here as well - a consumer sizing this up cannot read the backlog, and "current limitations" in choosing.md currently lists neither.]
