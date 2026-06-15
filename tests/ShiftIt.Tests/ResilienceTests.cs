using ShiftIt.Services;

namespace ShiftIt.Tests;

public sealed class ResilienceTests
{
    private static readonly TimeSpan Tiny = TimeSpan.FromMilliseconds(1);
    private static bool AlwaysTransient(Exception _) => true;

    [Fact]
    public async Task RunWithRetry_Succeeds_AfterTransientFailures()
    {
        var attempts = 0;
        var retries = 0;

        await Resilience.RunWithRetryAsync(
            action: () =>
            {
                attempts++;
                if (attempts < 3)
                {
                    throw new IOException("transient");
                }
                return Task.CompletedTask;
            },
            maxRetries: 5,
            baseDelay: Tiny,
            isTransient: AlwaysTransient,
            onRetry: (_, _) => retries++,
            cancellationToken: CancellationToken.None);

        Assert.Equal(3, attempts);  // failed twice, succeeded on the third
        Assert.Equal(2, retries);
    }

    [Fact]
    public async Task RunWithRetry_Rethrows_AfterExhaustingRetries()
    {
        var attempts = 0;

        await Assert.ThrowsAsync<IOException>(() => Resilience.RunWithRetryAsync(
            action: () => { attempts++; throw new IOException("always"); },
            maxRetries: 2,
            baseDelay: Tiny,
            isTransient: AlwaysTransient,
            onRetry: null,
            cancellationToken: CancellationToken.None));

        Assert.Equal(3, attempts);  // initial attempt + 2 retries
    }

    [Fact]
    public async Task RunWithRetry_DoesNotRetry_NonTransientErrors()
    {
        var attempts = 0;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Resilience.RunWithRetryAsync(
            action: () => { attempts++; throw new UnauthorizedAccessException(); },
            maxRetries: 5,
            baseDelay: Tiny,
            isTransient: _ => false,
            onRetry: null,
            cancellationToken: CancellationToken.None));

        Assert.Equal(1, attempts);  // not retried
    }
}
