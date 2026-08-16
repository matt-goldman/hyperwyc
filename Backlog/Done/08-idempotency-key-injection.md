# Issue 08 — Idempotency-Key Header Injection

> **Superseded by [issue 39](39-reconsider-idempotency.md); the behaviour below was
> removed.** Injecting the header only pays off if the backend implements it, which is an
> assumption a transport-level library should not make of an API it knows nothing about — and it
> conflicts with the "no architectural imposition" principle Hyperwyc is positioned on. Duplicate
> delivery turned out not to be a Hyperwyc-specific problem at all: any retry can cause it, and it
> is resolved between an application and its API. Kept for the record; the reasoning is worth
> preserving rather than erasing.

## Summary

Automatically inject an `Idempotency-Key` header on every mutating request (`POST`, `PUT`, `PATCH`, `DELETE`), using the envelope's `Id` (GUID) as the key value.

## Background

Replayed requests must be safe to deliver more than once. Injecting a stable `Idempotency-Key` lets the server deduplicate requests that arrive multiple times due to retries or connectivity issues. The key is set at envelope creation time and must be **identical** on both the initial send and any subsequent replay.

## Behaviour

1. When `HyperwycHandler` processes a mutating request:
   - If no `Idempotency-Key` header is present, inject one using the envelope's `Id`.
   - If the caller has already set an `Idempotency-Key`, preserve it and use the same value when creating/looking up the envelope.
2. The key must be the same value on initial send **and** on every retry/replay of the same envelope.
3. Read requests (`GET`, `HEAD`, `OPTIONS`) do not receive this header.

## Acceptance Criteria

- [x] `Idempotency-Key` header injected on all mutating requests before `base.SendAsync(...)` is called.
- [x] The header value matches `envelope.Id` exactly.
- [x] If the caller pre-sets `Idempotency-Key`, it is not overwritten; the envelope `Id` is set to the caller-supplied value instead.
- [x] On replay (sync flush), the same `Idempotency-Key` is re-injected from the stored envelope.
- [x] No `Idempotency-Key` header on read requests.
- [x] Unit tests cover: header absent → injected, header present → preserved, replayed envelope → same key, GET → no header.

## Notes

- Standard header name: `Idempotency-Key` (as per IETF draft-ietf-httpapi-idempotency-key-header).
- The envelope's `Id` is a UUID v4 generated at envelope construction time (see issue #03).
