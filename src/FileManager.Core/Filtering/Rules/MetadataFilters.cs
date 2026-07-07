using FileManager.Contracts.Profiles;
using System;

namespace FileManager.Core.Filtering.Rules;

internal sealed class SizeBoundsFilter(long? minSizeBytes, long? maxSizeBytes) : IFilter
{
    public string Description { get; } =
        $"Size bounds [{minSizeBytes?.ToString() ?? "0"}..{maxSizeBytes?.ToString() ?? "∞"}] bytes";

    public bool Excludes(in FilterInput input, out string reason)
    {
        if (minSizeBytes is long min && input.Metadata.Length < min)
        {
            reason = $"Size {input.Metadata.Length} B below minimum {min} B";
            return true;
        }
        if (maxSizeBytes is long max && input.Metadata.Length > max)
        {
            reason = $"Size {input.Metadata.Length} B above maximum {max} B";
            return true;
        }
        reason = string.Empty;
        return false;
    }
}

internal sealed class AgeFilter(
    TimeSpan? modifiedWithin, TimeSpan? modifiedOlderThan, TimeSpan? createdWithin,
    TimeProvider time) : IFilter
{
    public string Description { get; } = "Age filter (modified/created windows)";

    public bool Excludes(in FilterInput input, out string reason)
    {
        DateTimeOffset now = time.GetUtcNow();
        if (modifiedWithin is TimeSpan within && input.Metadata.LastWritten < now - within)
        {
            reason = $"Modified more than {within} ago";
            return true;
        }
        if (modifiedOlderThan is TimeSpan olderThan && input.Metadata.LastWritten > now - olderThan)
        {
            reason = $"Modified within the last {olderThan} (must be older)";
            return true;
        }
        if (createdWithin is TimeSpan createdWindow && input.Metadata.Created < now - createdWindow)
        {
            reason = $"Created more than {createdWindow} ago";
            return true;
        }
        reason = string.Empty;
        return false;
    }
}

/// <summary>Null settings apply the §4.4 defaults: hidden, system, and reparse points are all
/// excluded (Attributes.FollowSymlinks = false keeps symlinks out of Jobs entirely).</summary>
internal sealed class AttributeFilter(AttributeFilterSettings? settings) : IFilter
{
    private readonly bool _includeHidden = settings?.IncludeHidden ?? false;
    private readonly bool _includeSystem = settings?.IncludeSystem ?? false;
    private readonly bool _followSymlinks = settings?.FollowSymlinks ?? false;

    public string Description { get; } = "Attribute filter (hidden/system/symlink)";

    public bool Excludes(in FilterInput input, out string reason)
    {
        if (!_includeHidden && input.Metadata.IsHidden)
        {
            reason = "Hidden file (IncludeHidden is off)";
            return true;
        }
        if (!_includeSystem && input.Metadata.IsSystem)
        {
            reason = "System file (IncludeSystem is off)";
            return true;
        }
        if (!_followSymlinks && input.Metadata.IsSymlink)
        {
            reason = "Reparse point (FollowSymlinks is off)";
            return true;
        }
        reason = string.Empty;
        return false;
    }
}

internal sealed class DepthFilter(int maxDepth) : IFilter
{
    public string Description { get; } = $"Max depth {maxDepth}";

    public bool Excludes(in FilterInput input, out string reason)
    {
        if (input.Depth > maxDepth)
        {
            reason = $"Depth {input.Depth} exceeds MaxDepth {maxDepth}";
            return true;
        }
        reason = string.Empty;
        return false;
    }
}
