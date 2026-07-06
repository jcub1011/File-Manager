using FileManager.Core.Files;

namespace FileManager.Core.Filtering;

public readonly record struct FilterInput(
    string FullPath, string RelativePath, int Depth, FileMetadata Metadata);

public interface IFilter
{
    string Description { get; }                              // e.g. "Include glob *.wav|*.flac"
    bool Excludes(in FilterInput input, out string reason);
}
