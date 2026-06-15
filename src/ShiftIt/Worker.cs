using ShiftIt.Configuration;
using ShiftIt.Services;
using Microsoft.Extensions.Options;

namespace ShiftIt;

/// <summary>
/// Background service that runs an archive sweep on startup and then on a fixed
/// interval. A failure in one sweep is logged but never stops the loop.
/// </summary>
public class Worker : BackgroundService
{
    private readonly IArchiveScanner _scanner;
    private readonly IOptionsMonitor<ArchiveOptions> _options;
    private readonly ILogger<Worker> _logger;

    public Worker(
        IArchiveScanner scanner,
        IOptionsMonitor<ArchiveOptions> options,
        ILogger<Worker> logger)
    {
        _scanner = scanner;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(Math.Max(1, _options.CurrentValue.ScanIntervalMinutes));
        _logger.LogInformation("ShiftIt started. Sweep interval: {Interval}.", interval);

        try
        {
            // Run once immediately, then on each tick.
            await RunSweepSafelyAsync(stoppingToken);

            using var timer = new PeriodicTimer(interval);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await RunSweepSafelyAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        finally
        {
            _logger.LogInformation("ShiftIt stopping.");
        }
    }

    private async Task RunSweepSafelyAsync(CancellationToken stoppingToken)
    {
        try
        {
            await _scanner.RunSweepAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown in progress; let the loop exit.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Archive sweep failed; will retry next interval.");
        }
    }
}
