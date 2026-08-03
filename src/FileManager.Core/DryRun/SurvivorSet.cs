using FileManager.Core.Jobs;
using System;
using System.Collections.Generic;

namespace FileManager.Core.DryRun;

/// <summary>The set of destination paths a source writes to, probed by the destination sweep to
/// decide "is this pre-existing file an orphan". Semantically a <c>HashSet&lt;NormalizedPath&gt;</c> —
/// exact canonical strings under <see cref="NormalizedPath"/>'s comparison (OrdinalIgnoreCase on
/// Windows), populated only via <see cref="Add(NormalizedPath)"/> so every member went through
/// <c>NormalizedPath.Create</c>'s canonicalization.
///
/// <para>What the wrapper adds is the <b>span probe</b>: the sweep composes each enumerated file's
/// (directory, name) pair into a stack buffer and asks <see cref="Contains(ReadOnlySpan{char})"/> —
/// no per-file path string, no <see cref="NormalizedPath"/> wrapper, which at a 500k-file sweep is
/// the difference between ~100 MB of probe churn and none. The lookup is exact (same comparer over
/// the same strings), never hashed — a false "a source writes here" would silently omit a file from
/// a Mirror deletion preview, the one direction the preview must not fail in
/// (docs/dry-run-service-memory.md §11).</para></summary>
public sealed class SurvivorSet
{
    private readonly HashSet<string> _paths;
    private readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> _spanLookup;

    public SurvivorSet()
    {
        _paths = new HashSet<string>(NormalizedPath.ValueComparer);
        _spanLookup = _paths.GetAlternateLookup<ReadOnlySpan<char>>();
    }

    public int Count => _paths.Count;

    public void Add(NormalizedPath path)
    {
        if (path.Value is not null)
            _paths.Add(path.Value);
    }

    public bool Contains(NormalizedPath path) => path.Value is not null && _paths.Contains(path.Value);

    /// <summary>Span probe. <paramref name="canonicalFullPath"/> must already be canonical and carry
    /// no trailing separator (enumerated file paths never do) — members were normalized on the way
    /// in, and this deliberately does no per-probe normalization.</summary>
    public bool Contains(ReadOnlySpan<char> canonicalFullPath) => _spanLookup.Contains(canonicalFullPath);
}
