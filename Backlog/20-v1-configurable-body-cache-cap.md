# Issue 20 — [v1.0] Make `MaxCachedResponseBodyBytes` Publicly Configurable

## Summary

Expose `MaxCachedResponseBodyBytes` as a first-class configurable option in `hyperwycOptions`, replacing the hardcoded v0.1 default.

## Background

In v0.1, `MaxCachedResponseBodyBytes` defaults to 512 KB and is already wired through `hyperwycOptions` internally (see issue #17). This v1.0 issue makes that limit visible and documented in the public API surface so developers can raise or lower it per their app's needs.

## Acceptance Criteria

- [ ] `hyperwycOptions.MaxCachedResponseBodyBytes` is documented in XML comments and in the README.
- [ ] Changing the value in `Addhyperwyc()` options takes effect without code changes elsewhere.
- [ ] Value of `0` means "no limit" (or is explicitly disallowed with a clear exception — decide during implementation).
- [ ] Negative values throw `ArgumentOutOfRangeException` at configuration time.
- [ ] Unit test: setting a custom limit is respected by the cache write path.

## Notes

- The underlying code change is minimal (value is already read from options). This issue is primarily about documentation, validation, and surfacing the option deliberately in v1.0.
