using System.Data.Common;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace NinjaQueue.Execution;

/// <summary>Decides whether an exception is worth retrying (§7.3).</summary>
public interface IRetryClassifier
{
    /// <summary>True when a retry cannot fix the failure, so the job is dead-lettered at once.</summary>
    bool IsPermanent(Exception exception);
}

/// <summary>
/// The built-in classifier: <see cref="PermanentJobException"/>, payload/JSON errors and argument/validation errors are
/// permanent; database transient errors, timeouts, HTTP failures (429 and 5xx included) and anything else are retried.
/// </summary>
public sealed class DefaultRetryClassifier : IRetryClassifier
{
    /// <inheritdoc />
    public bool IsPermanent(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception switch
        {
            PermanentJobException or JsonException or NotSupportedException => true,
            ArgumentException => true,
            HttpRequestException { StatusCode: { } status } => (int)status is >= 400 and < 500 and not 408 and not 429 && status != HttpStatusCode.Conflict,
            DbException db => !db.IsTransient && IsConstraintViolation(db),
            _ => exception.GetType().Name == "FencedContractViolationException",
        };
    }

    // A constraint violation means the handler's data is wrong; retrying repeats it. Other database errors are retried.
    private static bool IsConstraintViolation(DbException exception)
        => exception.SqlState is "23502" or "23514"                    // PostgreSQL: not-null, check
           || exception.GetType().Name == "SqlException" && exception.Message.Contains("CHECK constraint", StringComparison.Ordinal);
}

/// <summary>Exponential backoff with full jitter: <c>random(0, min(cap, base * 2^(attempt-1)))</c> (§7.3).</summary>
public sealed class RetryBackoff(TimeSpan baseDelay, TimeSpan maxDelay)
{
    /// <summary>The delay before the retry that follows charged attempt <paramref name="attempt"/> (1-based).</summary>
    public TimeSpan Next(int attempt)
    {
        double exponent = Math.Min(Math.Max(attempt, 1) - 1, 30);
        double ceilingMs = Math.Min(maxDelay.TotalMilliseconds, baseDelay.TotalMilliseconds * Math.Pow(2, exponent));
        return TimeSpan.FromMilliseconds(RandomNumberGenerator.GetInt32(0, (int)Math.Max(1, Math.Min(int.MaxValue, ceilingMs))));
    }
}