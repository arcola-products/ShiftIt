using BenchmarkDotNet.Running;
using ShiftIt.Benchmarks;

// Run all benchmarks; pass --filter, --job short, etc. via args.
BenchmarkSwitcher.FromAssembly(typeof(FileMoveBenchmarks).Assembly).Run(args);
