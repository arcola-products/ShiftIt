namespace ShiftIt.Services;

/// <summary>Moves a single file to its mirrored archive destination.</summary>
public interface IFileMover
{
    /// <param name="sourcePath">Absolute path to the file in the hot root.</param>
    /// <param name="destinationPath">Absolute target path under the archive root.</param>
    /// <exception cref="PairAbortedException">
    /// The destination is full or persistently inaccessible; the caller should
    /// stop processing the current pair.
    /// </exception>
    Task<MoveResult> MoveAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken);
}
