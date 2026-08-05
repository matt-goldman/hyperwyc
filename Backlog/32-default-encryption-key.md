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
