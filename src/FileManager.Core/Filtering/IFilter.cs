using FileManager.Core.Files;

namespace FileManager.Core.Filtering;

/// <param name="NormalizedRelativePath">The '/'-normalized <paramref name="RelativePath"/>, computed
/// once by the caller so pattern filters need not re-normalize per rule. Null when the caller did not
/// pre-normalize, in which case pattern filters normalize on demand.</param>
public readonly record struct FilterInput(
    string FullPath, string RelativePath, int Depth, FileMetadata Metadata,
    string? NormalizedRelativePath = null);

public interface IFilter
{
    string Description { get; }                              // e.g. "Include glob *.wav|*.flac"
    bool Excludes(in FilterInput input, out string reason);
}
