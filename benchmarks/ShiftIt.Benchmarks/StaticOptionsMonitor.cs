using Microsoft.Extensions.Options;

namespace ShiftIt.Benchmarks;

/// <summary>Minimal fixed <see cref="IOptionsMonitor{T}"/> for benchmarks.</summary>
internal sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue { get; } = value;
    public T Get(string? name) => CurrentValue;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
