namespace ArchiveSync.Services;

/// <summary>Runs one full sweep across all configured hot-to-archive pairs.</summary>
public interface IArchiveScanner
{
    Task RunSweepAsync(CancellationToken cancellationToken);
}
