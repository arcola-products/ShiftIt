using Microsoft.Extensions.Options;

namespace ShiftIt.Configuration;

/// <summary>
/// Validates relationships that data annotations cannot express: rooted paths,
/// distinct hot/archive roots, and an archive root that is not nested inside its
/// own hot root (which would cause a file to be re-scanned forever).
/// </summary>
public sealed class ArchiveOptionsValidator : IValidateOptions<ArchiveOptions>
{
    public ValidateOptionsResult Validate(string? name, ArchiveOptions options)
    {
        var errors = new List<string>();

        if (options.Pairs is null || options.Pairs.Count == 0)
        {
            return ValidateOptionsResult.Fail("Archive:Pairs must contain at least one entry.");
        }

        for (var i = 0; i < options.Pairs.Count; i++)
        {
            var pair = options.Pairs[i];
            var label = string.IsNullOrWhiteSpace(pair.Name) ? $"Pairs[{i}]" : pair.Name;

            if (string.IsNullOrWhiteSpace(pair.HotRoot) || !Path.IsPathRooted(pair.HotRoot))
            {
                errors.Add($"{label}: HotRoot must be an absolute path.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(pair.ArchiveRoot) || !Path.IsPathRooted(pair.ArchiveRoot))
            {
                errors.Add($"{label}: ArchiveRoot must be an absolute path.");
                continue;
            }

            var hot = NormalizeRoot(pair.HotRoot);
            var archive = NormalizeRoot(pair.ArchiveRoot);

            if (PathsEqual(hot, archive))
            {
                errors.Add($"{label}: HotRoot and ArchiveRoot must be different.");
            }
            else if (IsNestedWithin(archive, hot))
            {
                errors.Add($"{label}: ArchiveRoot must not be located inside HotRoot.");
            }
        }

        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors);
    }

    private static string NormalizeRoot(string path)
    {
        var full = Path.GetFullPath(path);
        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="candidate"/> is the same as or under <paramref name="root"/>.</summary>
    private static bool IsNestedWithin(string candidate, string root)
    {
        var prefix = root + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}
