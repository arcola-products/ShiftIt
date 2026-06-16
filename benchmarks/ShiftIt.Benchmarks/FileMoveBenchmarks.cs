using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.InProcess.Emit;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShiftIt.Configuration;
using ShiftIt.Services;

namespace ShiftIt.Benchmarks;

/// <summary>Archive destination for a benchmark run.</summary>
public enum Target
{
    /// <summary>Local disk (same machine as the source).</summary>
    Local,

    /// <summary>SMB share, exercising a real network round-trip.</summary>
    Smb,
}

/// <summary>
/// Measures <see cref="FileMover.MoveAsync"/> (copy -> verify -> rename -> delete)
/// across file sizes, with hash verification off/on, to a local and an SMB
/// destination. The source (always local) is recreated in IterationSetup so it
/// is not part of the measurement; each measured invocation performs one move.
/// </summary>
[MemoryDiagnoser]
[Config(typeof(Config))]
public class FileMoveBenchmarks
{
    // The SMB share used for the network destination (must be writable).
    private const string SmbRoot = @"S:\The Archive\temp";

    private sealed class Config : ManualConfig
    {
        public Config() => AddJob(Job.Default
            // In-process keeps the run self-contained; for publication-grade
            // numbers, drop this to use the default out-of-process toolchain.
            .WithToolchain(InProcessEmitToolchain.Instance)
            .WithInvocationCount(1)   // each move consumes its source
            .WithUnrollFactor(1)
            .WithWarmupCount(2)
            .WithIterationCount(6));
    }

    /// <summary>File sizes in bytes: 4 KB, 256 KB, 4 MB, 64 MB.</summary>
    [Params(4 * 1024, 256 * 1024, 4 * 1024 * 1024, 64 * 1024 * 1024)]
    public int FileSize;

    [Params(false, true)]
    public bool VerifyWithHash;

    [Params(Target.Local, Target.Smb)]
    public Target Destination;

    private FileMover _mover = null!;
    private string _sourceRoot = null!;
    private string _archiveRoot = null!;
    private string _source = null!;
    private string _dest = null!;
    private byte[] _payload = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        var options = new ArchiveOptions { VerifyWithHash = VerifyWithHash, MaxRetries = 0 };
        _mover = new FileMover(
            NullLogger<FileMover>.Instance,
            new StaticOptionsMonitor<ArchiveOptions>(options));

        var id = Guid.NewGuid().ToString("N");
        _sourceRoot = Path.Combine(Path.GetTempPath(), "shiftit-bench", id);   // source always local
        var archiveBase = Destination == Target.Smb ? SmbRoot : Path.GetTempPath();
        _archiveRoot = Path.Combine(archiveBase, "shiftit-bench-archive", id);
        Directory.CreateDirectory(_sourceRoot);
        Directory.CreateDirectory(_archiveRoot);

        _payload = new byte[FileSize];
        new Random(42).NextBytes(_payload);

        _source = Path.Combine(_sourceRoot, "source.bin");
        _dest = Path.Combine(_archiveRoot, "archive", "source.bin");
    }

    [IterationSetup]
    public void IterationSetup()
    {
        // Fresh source for each measured move; clear any prior destination.
        // The scanner owns directory creation, so ensure the dir exists here.
        File.WriteAllBytes(_source, _payload);
        var destDir = Path.GetDirectoryName(_dest)!;
        if (Directory.Exists(destDir))
        {
            Directory.Delete(destDir, recursive: true);
        }
        Directory.CreateDirectory(destDir);
    }

    [Benchmark]
    public async Task Move() =>
        await _mover.MoveAsync(_source, _dest, CancellationToken.None);

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        TryDeleteTree(_sourceRoot);
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
