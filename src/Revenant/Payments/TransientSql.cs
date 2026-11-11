using Npgsql;

namespace Revenant.Payments;

/// <summary>
/// Bounded retry of a whole ledger transaction. Only PostgreSQL serialization failure (<c>40001</c>) and deadlock
/// (<c>40P01</c>) are retried: in both cases the server rolled the transaction back, so running the same command again,
/// with the same command id or claim, cannot double an effect. Every other error, including the ledger's own RV00x
/// refusals, unique violations and connection loss, is returned to the caller unchanged.
/// </summary>
public static class TransientSql
{
    public const int MaxAttempts = 3;

    public static bool IsRetryable(Exception exception) =>
        exception is PostgresException { SqlState: PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected };

    public static async Task<T> RetryAsync<T>(Func<Task<T>> action, CancellationToken ct, int maxAttempts = MaxAttempts)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await action();
            }
            catch (Exception ex) when (attempt < maxAttempts && IsRetryable(ex))
            {
                // Short jittered backoff so two colliding transactions do not collide again in lockstep.
                await Task.Delay(TimeSpan.FromMilliseconds((10 * attempt * attempt) + Random.Shared.Next(0, 10)), ct);
            }
        }
    }
}
