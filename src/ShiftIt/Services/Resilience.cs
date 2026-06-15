namespace ShiftIt.Services;

/// <summary>Retries an operation on transient failures with exponential backoff.</summary>
public static class Resilience
{
    /// <param name="action">The operation to run.</param>
    /// <param name="maxRetries">Maximum retries after the first attempt.</param>
    /// <param name="baseDelay">Delay before the first retry; doubles each attempt.</param>
    /// <param name="isTransient">Returns true for exceptions worth retrying.</param>
    /// <param name="onRetry">Invoked before each retry (attempt number, exception).</param>
    public static async Task RunWithRetryAsync(
        Func<Task> action,
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
                await action();
                return;
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
