using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.InProcess.Emit;
using Microsoft.Extensions.Logging.Abstractions;
using ShiftIt.Services;

namespace ShiftIt.Benchmarks;

/// <summary>
/// Measures <see cref="FileMover.MoveAsync"/> (copy -> verify -> rename -> delete)
/// across a range of file sizes, with hash verification off and on. The source
/// file is (re)created in IterationSetup so it is not part of the measurement;
/// each measured invocation performs exactly one move.
/// </summary>
[MemoryDiagnoser]
[Config(typeof(Config))]
public class FileMoveBenchmarks
{
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

    private FileMover _mover = null!;
    private string _root = null!;
    private string _source = null!;
    private string _dest = null!;
    private byte[] _payload = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _mover = new FileMover(NullLogger<FileMover>.Instance);
        _root = Path.Combine(Path.GetTempPath(), "shiftit-bench", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        _payload = new byte[FileSize];
        new Random(42).NextBytes(_payload);

        _source = Path.Combine(_root, "source.bin");
        _dest = Path.Combine(_root, "archive", "source.bin");
    }

    [IterationSetup]
    public void IterationSetup()
    {
        // Fresh source for each measured move; clear any prior destination.
        File.WriteAllBytes(_source, _payload);
        var destDir = Path.GetDirectoryName(_dest)!;
        if (Directory.Exists(destDir))
        {
            Directory.Delete(destDir, recursive: true);
        }
    }

    [Benchmark]
    public async Task Move() =>
        await _mover.MoveAsync(_source, _dest, VerifyWithHash, CancellationToken.None);

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }
}
