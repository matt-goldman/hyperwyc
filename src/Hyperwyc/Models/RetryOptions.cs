namespace Hyperwyc.Models;

/// <summary>
/// Configures the retry behaviour for a queued outbox request.
/// </summary>
/// <param name="MaxRetries">
/// Maximum number of retry attempts before the request is moved to the dead-letter
/// queue. Use <c>0</c> to disable retries.
/// </param>
/// <param name="InitialDelay">
/// The delay before the first retry attempt.
/// </param>
/// <param name="BackoffMultiplier">
/// Multiplier applied to <paramref name="InitialDelay"/> on each successive retry.
/// A value of <c>1.0</c> produces a constant interval; values greater than <c>1.0</c>
/// produce exponential back-off.
/// </param>
public record RetryOptions(
    int MaxRetries,
    TimeSpan InitialDelay,
    double BackoffMultiplier);
