# Issue 32 — Default Encryption Key for the Zero-Config Store

## Summary

Decide how the default `CabinetSyncStore` protects data at rest when the consumer supplies no
key. Making Cabinet the zero-configuration default (issue #31) promotes today's path-derived
key from an explicit opt-in to the setting almost every consumer will silently ship.

## Background

`CabinetSyncStore`'s single-argument constructor derives an AES-256-GCM key from the store
directory path via SHA-256. Its own XML documentation says:

> The derived key is deterministic for a given path but is not cryptographically strong.
> For production use, supply an explicit key via the two-parameter overload.

That caveat is acceptable while the store is an explicit choice — the developer selecting
`new CabinetSyncStore("hyperwyc.db")` is at least in a position to read the doc comment. Once
issue #31 makes it the default that `AddHyperwyc()` resolves with no arguments, the weak key
becomes what every consumer gets unless they go looking for a reason not to.

The store holds cached API responses and queued writes — application data, and on the write
path, data that has not yet reached the server.

The tension is real and inherent: **"works out of the box" and "secure by default" pull in
opposite directions here.** A prompt for a key breaks zero-config; a silent weak default
undersells the encryption the library advertises. This item exists so the trade-off is chosen
rather than inherited.

## Decision

**Option A — keep the path-derived key as the free default, and document how to improve on it.**

A weaker protection that costs the consumer nothing is acceptable out of the box, provided the
library is straightforward about what it does and does not guarantee, and provided the upgrade
path is documented where people will actually meet it. Encryption at rest stops being a
headline feature and becomes what it is: a meaningful obstacle to casual inspection of the
device's filesystem, not a defence against a determined attacker with the device in hand.

The work is therefore documentation and honest phrasing, not cryptography.

Option D below (a pluggable key provider) remains available later as a purely additive change —
an optional provider with the derived key as its default breaks nothing — so choosing A now
does not close it off.

## Candidate Approaches

### Option A — Keep the derived key; document loudly *(chosen)*
Zero-config uses the path-derived key. README and XML docs state plainly that production apps
should supply their own key, and where to get one. Cheapest, and honest, but relies on
consumers reading documentation about a default they never had to think about.

### Option B — Generate a random key on first run; persist it via platform secure storage
Strongest, and what a security-conscious implementation would do. Blocked by the same coupling
problem as issue #13: `SecureStorage` is MAUI Essentials, Keychain is iOS, DPAPI is Windows.
The core cannot reach any of them without platform targets.

### Option C — Random key on first run, stored alongside the database
Better than derived-from-path against casual inspection, but the key sits next to the data it
protects, so it defends only against an attacker who has one and not the other. Arguably worse
than Option A because it *looks* stronger than it is.

### Option D — Pluggable key provider, defaulting to A
Introduce a small `IEncryptionKeyProvider` (or a `Func<byte[]>` option) that defaults to the
derived key, and document a MAUI `SecureStorage` implementation as reference code — the same
pattern issue #13 settles on for connectivity. Preserves zero-config, gives security-conscious
consumers a first-class extension point, and keeps platform APIs out of the core.

## Acceptance Criteria

- [ ] Zero-configuration `AddHyperwyc()` works with no key-related ceremony.
- [ ] README documents what protects the store by default, in plain terms, and shows how to
      supply an explicit key — placed where a reader meets it during setup, not in an appendix.
- [ ] The `CabinetSyncStore` "not cryptographically strong" caveat is surfaced on the path
      consumers actually take. Today it appears only on a constructor most will never call, so
      after issue #31 the warning would sit on the one code path nobody reads.
- [ ] Feature descriptions of encryption at rest — README, package description, TECHNICAL_PLAN
      §9 — are phrased so they do not imply a stronger guarantee than the default provides.
- [ ] A MAUI `SecureStorage`-backed key is documented as reference code, alongside the
      connectivity reference implementation from issue #13.

## Notes

- Filed alongside issue #31, which is what changes the blast radius of the current default.
- Scope after the decision is documentation and phrasing, so this can land with #31 rather than
  waiting for a v1.0 security pass.
- Related to issue #30 (sensitive headers persisted): #30 governs what goes into the store,
  this item governs how well what is in there is protected. Both should be resolved before a
  1.0 that claims encryption at rest as a feature.

## Related: the derived key is not stable across an iOS restore

Found while writing [issue 48](48-exclude-store-from-os-backup.md), and unverified on device.

`DeriveKey` is `SHA256(UTF8(DirectoryPath))` over the absolute path. On iOS that path contains
the app container UUID, which changes when the app is reinstalled or restored onto a new device.
The restored files are therefore at a different path, deriving a different key, and cannot be
decrypted.

Two consequences for this issue:

- It is a defect in the default independent of anything else: a restore-with-reinstall silently
  loses the store. Needs confirming on device before it is treated as fact.
- It strengthens the case for the `SecureStorage`-backed key this issue is about, since a key
  held in the keychain survives the move. But note the inversion recorded in 48: a stable key
  also removes the accidental protection against a restored outbox replaying delivered writes.
  The two issues want reading together.

## Decide the derivation before [issue 49](49-unreadable-store-recovery.md)

49 covers what Hyperwyc does with a store it cannot decrypt. How much of that machinery is worth
building depends on how often the path is reached, which this issue controls.

Deriving the default key from a **stable** input — a fixed salt plus an application identity —
rather than from the volatile absolute path would make the store survive the iOS reinstall and
restore cases that currently break it. The derived-key failure mode then reduces to genuine
corruption, which is rare and affects single documents rather than the whole store.

Nothing is released, so changing the derivation costs no compatibility.
