namespace ShiftIt.Services;

/// <summary>Retries an operation on transient failures with exponential backoff.</summary>
public static class Resilience
{
    /// <param name="action">The operation to run.</param>
    /// <param name="maxRetries">Maximum retries after the first attempt.</param>
    /// <param name="baseDelay">Delay before the first retry; doubles each attempt.</param>
    /// <param name="isTransient">Returns true for exceptions worth retrying.</param>
    /// <param name="onRetry">Invoked before each retry (attempt number, exception).</param>
    public static Task RunWithRetryAsync(
        Func<Task> action,
        int maxRetries,
        TimeSpan baseDelay,
        Func<Exception, bool> isTransient,
        Action<int, Exception>? onRetry,
        CancellationToken cancellationToken) =>
        RunWithRetryAsync<object?>(
            async () => { await action(); return null; },
            maxRetries, baseDelay, isTransient, onRetry, cancellationToken);

    /// <summary>Retry variant that returns the operation's result.</summary>
    public static async Task<T> RunWithRetryAsync<T>(
        Func<Task<T>> action,
        int maxRetries,
        TimeSpan baseDelay,
        Func<Exception, bool> isTransient,
        Action<int, Exception>? onRetry,
        CancellationToken cancellationToken)
    {
        var attempt = 0;
        while (true)
        {
            try
            {
                return await action();
            }
            catch (Exception ex) when (attempt < maxRetries && isTransient(ex))
            {
                attempt++;
                onRetry?.Invoke(attempt, ex);

                // baseDelay * 2^(attempt-1), e.g. 2s, 4s, 8s...
                var delay = TimeSpan.FromTicks(baseDelay.Ticks * (1L << (attempt - 1)));
                await Task.Delay(delay, cancellationToken);
            }
        }
    }
}
