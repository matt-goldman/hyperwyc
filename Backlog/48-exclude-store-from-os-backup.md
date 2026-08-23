# Issue 48 — Excluding the Store from OS Backup

## Summary

On iOS and Android the Hyperwyc store sits in a location the operating system backs up to iCloud
or Google Drive by default. Consumers should usually exclude it, and Hyperwyc should tell them
how. Documentation, not implementation — but the reason is stronger than "it wastes backup
quota", and the write-up should lead with the real hazard.

## Status

⬜ Open. Filed 2026-08-23 from a question about issue 16.

## Why this matters more than storage

Three consequences, in descending order of how much damage they do.

### 1. A restored outbox replays stale writes

This is the sharp edge, and it is a correctness problem rather than a housekeeping one.

The outbox is a durable list of *writes that have not happened yet*. Backing it up captures that
list at a moment in time; restoring it re-asserts that those writes are still pending. Neither is
true after the fact:

- Restore a three-week-old backup onto a wiped device and Hyperwyc will faithfully replay a sale
  that was delivered two weeks ago, or one the user cancelled.
- Restore onto a *second* device and both devices now hold the same pending writes. Both will
  send them.

Hyperwyc has no duplicate suppression by design
([ADR 0001](../docs/decisions/0001-idempotency-is-not-hyperwycs-remit.md)), so nothing catches
this. It is the application's API that eats the duplicate order.

Worth being precise about the blame here: Hyperwyc is behaving correctly at every step. It was
handed a durable outbox and asked to deliver what is in it, and it does. The defect is that the
outbox was duplicated by a system Hyperwyc knows nothing about, which is exactly why this needs
to be documented rather than defended against.

### 2. Android's backup quota is 25 MB, and blowing it fails silently

Android Auto Backup caps an app at 25 MB of backed-up data. Exceed it and **backup stops for the
whole app** — not just for the offending files, and with no user-visible error. A response cache
that grows past the cap therefore takes the app's settings and databases down with it.

`MaxCachedResponseBodyBytes` caps a single entry at 512 KB, but nothing caps the total: cache
eviction is [issue 42](42-cache-eviction.md), still open. Fifty cached responses is enough to be
a meaningful fraction of the budget. This is the argument that applies even to consumers who do
not care about the outbox.

### 3. Cached responses are restored stale, onto a device they may not describe

Least severe, because `TtlStalenessEvaluator` already treats an old `CachedAt` as stale and
refetches. Worth a sentence, not a section.

## The interaction with the path-derived encryption key

Found while writing this up, and it deserves its own attention.

`CabinetSyncStore.DeriveKey` is `SHA256(UTF8(DirectoryPath))`, over the *absolute* path. On iOS
that path lives under `/var/mobile/Containers/Data/Application/<UUID>/…`, and **that UUID is not
stable across a reinstall or a restore onto a new device.** Restoring a backup therefore puts the
files back at a different absolute path, which derives a different key, which cannot decrypt
them.

So on iOS, with the default key, a restored store is unreadable.

Two things follow:

- It accidentally mitigates hazard 1 above. That is not a defence — an accident that depends on
  an implementation detail of key derivation is not a design, and it stops working the moment a
  consumer supplies their own stable key from secure storage, which is precisely what
  [issue 32](32-default-encryption-key.md) recommends. **The better a consumer's key management,
  the more exposed they are to the replay hazard.** That inversion is the thing to document.
- It is a defect in its own right, independent of backup: any iOS reinstall-with-restore silently
  loses the store. Needs verifying on device, then recording against 32.
- What Hyperwyc *does* on encountering that store is [issue 49](49-unreadable-store-recovery.md),
  which is a real defect rather than documentation: today it throws a raw `CryptographicException`
  from wherever the store is first read. 49 settles on reporting it and degrading to an empty
  store — Hyperwyc does not delete the files or refuse to start, since neither is a
  transport-level decision.

Android is unaffected — `/data/user/0/<package>/…` is stable, because the package name does not
change.

## How consumers actually exclude it

Both platforms support **path-level** exclusion, not just app-level. Worth stating plainly,
because the app-level switches are blunt instruments nobody should reach for first.

### iOS

Set the excluded-from-backup resource value on the store directory, once, at startup:

```csharp
// Before the store is first resolved.
var url = NSUrl.FromFilename(CabinetStoreOptions.DefaultDirectoryPath());
url.SetResource(NSUrl.IsExcludedFromBackupKey, NSNumber.FromBoolean(true));
```

This is Apple's documented mechanism, and their data-storage guidelines expect it for anything
regenerable. The directory must exist first, so ordering against Hyperwyc's lazy store
construction matters — see Open Questions.

### Android

Exclude the path in the backup rules rather than turning backup off. API 31+:

```xml
<!-- res/xml/data_extraction_rules.xml -->
<data-extraction-rules>
    <cloud-backup>
        <exclude domain="file" path=".local/share/Hyperwyc/" />
    </cloud-backup>
    <device-transfer>
        <exclude domain="file" path=".local/share/Hyperwyc/" />
    </device-transfer>
</data-extraction-rules>
```

Below API 31 the equivalent is `<full-backup-content>` with the same `<exclude>` element, and
both are wired up through `<application android:dataExtractionRules=… android:fullBackupContent=…>`.

The `<cloud-backup>` / `<device-transfer>` split is worth pointing out: a consumer might
reasonably allow a direct device-to-device transfer, where the old device is being retired and
duplicate replay is not a risk, while excluding cloud backup, where it is. That is a judgement
about their domain, not ours.

`android:allowBackup="false"` also works and is a bigger hammer than this warrants.

## Why documentation and not implementation

Through [the scope test](../docs/decisions/README.md#the-standing-scope-test):

| Question | Answer |
|---|---|
| Would the problem exist without Hyperwyc? | **Mostly yes** — any app with local state faces it. But Hyperwyc creates a directory the consumer never asked for, in a backed-up location, and may be the only thing in the app holding a *pending write queue* |
| Does it require anything of the consumer's API? | No |
| Can the application already do it? | **Yes** — an `NSUrl` call and a manifest file. Neither is hard once you know the path |
| Does it depend on something only Hyperwyc knows? | **The path does.** That is Hyperwyc's choice, and it is the one thing the consumer cannot work out for themselves |

So the split falls where it did for [issue 13](Done/13-connectivity-reference-implementation.md):
Hyperwyc makes the path knowable and explains the consequence; the consumer applies it. Doing the
iOS exclusion in the library would need iOS APIs, which is the same MAUI-dependency argument that
kept `MauiConnectivityService` out of the package.

`CabinetStoreOptions.DefaultDirectoryPath()` is already public, so the path is already reachable.
Nothing needs to be built for the documentation to be actionable.

## Acceptance Criteria

- [ ] README documents the exposure, leading with stale-outbox replay rather than disk usage.
- [ ] README gives the iOS `NSURLIsExcludedFromBackupKey` recipe against
      `CabinetStoreOptions.DefaultDirectoryPath()`.
- [ ] README gives the Android `data_extraction_rules` recipe, including the
      cloud-backup vs device-transfer distinction.
- [ ] The Android exclusion path is **verified against a real device**, not inferred — see
      Open Questions.
- [ ] The inverted-risk note is recorded: a consumer with good key management loses the accidental
      protection the derived key provides.
- [ ] Cross-referenced from [issue 32](32-default-encryption-key.md) and
      [issue 42](42-cache-eviction.md).
- [ ] The iOS key-derivation defect is either confirmed and filed against 32, or ruled out.

## Open Questions

1. **What does `LocalApplicationData` actually resolve to on each platform?** The Android XML
   above assumes `{FilesDir}/.local/share/Hyperwyc`, which is what .NET for Android's XDG-style
   mapping should give, and the iOS path assumes `Library/Application Support`. Both are stated
   from memory and **must be confirmed by printing the path on device** before they go in the
   README — a wrong path in an exclusion rule fails silently, which is the worst possible failure
   for this particular piece of advice.
2. **Ordering on iOS.** The directory must exist before the backup flag can be set, and Hyperwyc
   deliberately does not create it until the store is first resolved (issue 31). Does the
   consumer resolve `ISyncStore` early to force creation, or should `DefaultDirectoryPath()` gain
   a companion that creates the directory? The second is a small, honest addition; the first is
   free but ugly.
3. **Should this be more than documentation for the outbox specifically?** A stored "device
   identity" or "outbox generation" written at first run, checked at startup, and treated as
   *this outbox belongs to a different installation — discard it* would defend against restore
   replay directly. That is real design work and probably fails the scope test, but it is the
   only mechanism that actually solves hazard 1 rather than delegating it. Worth deciding
   deliberately rather than by omission.

## Notes

- Raised by the author while reviewing [issue 16](Done/16-reset-store-async.md), on the observation
  that mobile consumers may want the store kept out of iCloud and Google Drive backups.
- Suggested milestone **v1.2**. It is documentation and cheap, but the hazard is real enough that
  it should not wait for a milestone where it might be deprioritised. It could reasonably land
  alongside the remaining v0.1 documentation work instead.
- Question 3 is the one to revisit if a consumer ever reports a duplicate-order incident. That
  report would change the answer.
