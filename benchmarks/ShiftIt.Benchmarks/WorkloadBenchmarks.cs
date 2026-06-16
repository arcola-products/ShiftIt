using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.InProcess.Emit;
using Microsoft.Extensions.Logging.Abstractions;
using ShiftIt.Configuration;
using ShiftIt.Services;

namespace ShiftIt.Benchmarks;

/// <summary>Shape of the generated hot tree for a workload run.</summary>
public enum Workload
{
    /// <summary>Many tiny files spread across nested folders.</summary>
    ManySmall,

    /// <summary>A handful of large files.</summary>
    FewLarge,

    /// <summary>A deeper nested tree mixing mostly-small with a few large files.</summary>
    Mixed,
}

/// <summary>
/// Drives the whole pipeline — <see cref="ArchiveScanner.RunSweepAsync"/> over a
/// realistic, multi-nested tree of mixed file sizes — to a local and an SMB
/// destination, sequential and parallel. This is the end-to-end throughput
/// benchmark; the tree is rebuilt each iteration (and excluded from timing).
/// </summary>
[MemoryDiagnoser]
[Config(typeof(Config))]
public class WorkloadBenchmarks
{
    private const string SmbRoot = @"S:\The Archive\temp";
    private const int MaxFileSize = 20 << 20; // 20 MiB

    // Folders at varying depths; the empty entry is the hot root itself.
    private static readonly string[] Folders =
    [
        "", "a", "a/b", "a/b/c", "d", "d/e", "f/g/h",
    ];

    private sealed class Config : ManualConfig
    {
        public Config() => AddJob(Job.Default
            .WithToolchain(InProcessEmitToolchain.Instance)
            .WithInvocationCount(1)   // each sweep consumes the hot tree
            .WithUnrollFactor(1)
            .WithWarmupCount(1)
            .WithIterationCount(3));
    }

    [Params(Workload.ManySmall, Workload.FewLarge, Workload.Mixed)]
    public Workload Shape;

    [Params(Target.Local, Target.Smb)]
    public Target Destination;

    [Params(1, 8)]
    public int Parallelism;

    private IArchiveScanner _scanner = null!;
    private string _hotRoot = null!;
    private string _archiveRoot = null!;
    private byte[] _payload = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        var id = Guid.NewGuid().ToString("N");
        _hotRoot = Path.Combine(Path.GetTempPath(), "shiftit-wl", id, "hot");
        var archiveBase = Destination == Target.Smb ? SmbRoot : Path.GetTempPath();
        _archiveRoot = Path.Combine(archiveBase, "shiftit-wl-archive", id);
        Directory.CreateDirectory(_hotRoot);
        Directory.CreateDirectory(_archiveRoot);

        // One buffer reused for every file (sliced to size) to avoid setup allocs.
        _payload = new byte[MaxFileSize];
        new Random(42).NextBytes(_payload);

        var options = new ArchiveOptions
        {
            MinAgeDays = 0,                  // everything is eligible
            MaxParallelMoves = Parallelism,
            MaxRetries = 0,
            RemoveEmptyHotFolders = true,
            VerifyWithHash = false,
            Pairs = [new TargetPair { Name = "Bench", HotRoot = _hotRoot, ArchiveRoot = _archiveRoot }],
        };
        var monitor = new StaticOptionsMonitor<ArchiveOptions>(options);
        var mover = new FileMover(NullLogger<FileMover>.Instance, monitor);
        _scanner = new ArchiveScanner(mover, monitor, NullLogger<ArchiveScanner>.Instance);
    }

    [IterationSetup]
    public void IterationSetup()
    {
        // Rebuild the hot tree (the previous sweep moved it away) and clear the
        // archive so each measured sweep moves the full set fresh.
        ResetDirectory(_hotRoot);
        ResetDirectory(_archiveRoot);
        BuildWorkload(_hotRoot, Shape);
    }

    [Benchmark]
    public Task Sweep() => _scanner.RunSweepAsync(CancellationToken.None);

    private void BuildWorkload(string root, Workload shape)
    {
        var rng = new Random(7);
        switch (shape)
        {
            case Workload.ManySmall:
                Distribute(root, count: 250, _ => 8 << 10);
                break;
            case Workload.FewLarge:
                Distribute(root, count: 3, _ => 20 << 20);
                break;
            case Workload.Mixed:
                Distribute(root, count: 100, i => i % 20 == 0
                    ? 2 << 20
                    : rng.Next(4) switch { 0 => 4 << 10, 1 => 16 << 10, 2 => 64 << 10, _ => 8 << 10 });
                break;
        }
    }

    private void Distribute(string root, int count, Func<int, int> sizeOf)
    {
        for (var i = 0; i < count; i++)
        {
            var folder = Folders[i % Folders.Length];
            var dir = folder.Length == 0
                ? root
                : Path.Combine(root, folder.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, $"f{i}.bin"), _payload.AsSpan(0, sizeOf(i)));
        }
    }

    private static void ResetDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
        Directory.CreateDirectory(path);
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        TryDeleteTree(Path.GetDirectoryName(_hotRoot)!);
        TryDeleteTree(_archiveRoot);
    }

    private static void TryDeleteTree(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }
}
