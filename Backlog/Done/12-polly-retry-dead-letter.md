# Issue 12 — Polly Retry Policy with Exponential Backoff and Dead-Letter

## Summary

Implement retry logic for outbound requests that fail after the initial send. Use Polly for exponential backoff. After the retry threshold is exhausted, move the envelope to dead-letter and publish `OnFailed`.

## Background

A request can fail even when the device is technically "online" — captive portals, DNS failures, transient API outages, and server errors all produce failures that fall outside the connectivity model. Polly's retry policy handles these cases without involvement from `IConnectivityService`.

## Behaviour

### Retry policy
- Default: up to **5 retries** with exponential backoff starting at 2 seconds (i.e. 2s, 4s, 8s, 16s, 32s).
- Configurable globally via `RestycOptions.DefaultRetryOptions` and per-endpoint via `ISyncPolicy.GetRetryOptions(request)`.
- Jitter should be applied to avoid thundering herd (use Polly's `DecorrelatedJitterBackoffV2`).
- Before each retry attempt, publish `OnRetrying`.

### Dead-letter
- After all retries are exhausted, call `ISyncStore.MoveToDeadLetterAsync(id)`.
- Publish `OnFailed`.
- Dead-lettered envelopes are not retried automatically. Manual requeue is a v1.0 feature (issue #23).

### Failure definition
- **Failure:** non-2xx HTTP response, `HttpRequestException`, or `TaskCanceledException` (timeout).
- **Not a failure:** the device losing connectivity mid-flight — `IConnectivityService` will signal reconnection and a new flush will pick up the envelope.

## Acceptance Criteria

- [x] Polly retry pipeline configured in `src/Restyc` (add `Polly` NuGet dependency).
- [x] `RetryOptions` from `ISyncPolicy.GetRetryOptions` applied per-request.
- [x] `OnRetrying` published before each attempt.
- [x] After max retries: `MoveToDeadLetterAsync` called, `OnFailed` published.
- [x] Jitter applied to backoff intervals.
- [x] Dead-lettered envelopes are not re-enqueued on next flush.
- [x] Unit tests cover: success on retry N, exhaustion → dead-letter, `OnFailed` event, custom retry options.

## Notes

- Consider using `Polly.Extensions.Http` or the `Polly` v8 `ResiliencePipeline` API.
- The retry policy wraps the HTTP send step inside the flush orchestrator (issue #11), not `RestycHandler` directly.
- Polly v8 `DelayBackoffType.Exponential` with `UseJitter = true` implements `DecorrelatedJitterBackoffV2` internally.
- Polly requires `MaxRetryAttempts >= 1`; when `RetryOptions.MaxRetries = 0` the retry strategy is omitted and the request is attempted once with no retries.
