using Microsoft.Extensions.Options;

namespace ShiftIt.Tests.TestHelpers;

/// <summary>
/// Minimal <see cref="IOptionsMonitor{T}"/> that always returns a fixed value,
/// for injecting options into services under test without the DI container.
/// </summary>
public sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue { get; } = value;
    public T Get(string? name) => CurrentValue;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
