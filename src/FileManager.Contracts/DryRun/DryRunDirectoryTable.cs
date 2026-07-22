using System;
using System.Collections.Generic;
using System.IO;

namespace FileManager.Contracts.DryRun;

/// <summary>Builds a report's shared <see cref="DryRunDirectory"/> table, splitting absolute paths
/// into (directory index, file name) wire triples. Entries are emitted once, on first use, ancestors
/// first — so <see cref="DryRunDirectory.ParentIndex"/> always refers to an earlier entry and a
/// consumer can materialize paths in one forward pass. One builder owns one report's whole index
/// space: the streaming producer keeps a single instance across every chunk (slicing per-chunk news
/// off with <see cref="FlushNew"/>), the batched producer uses <see cref="Mark"/>/<see cref="RollbackTo"/>
/// to back a rejected bundle's directories out of the table when its byte-budget reservation fails.
/// Dedup is <see cref="StringComparer.Ordinal"/> — case-insensitive folding would change displayed
/// path casing whenever the first-seen casing differed from a later one.</summary>
public sealed class DryRunDirectoryTableBuilder
{
    private readonly Dictionary<string, int> _indexByPath = new(StringComparer.Ordinal);
    private readonly List<DryRunDirectory> _entries = [];
    private readonly List<string> _paths = [];   // parallel to _entries; keys to remove on rollback
    private int _flushed;

    /// <summary>Every entry added so far, in index order.</summary>
    public IReadOnlyList<DryRunDirectory> Entries => _entries;

    public int Count => _entries.Count;

    /// <summary>The index for an absolute directory path, adding it and every missing ancestor
    /// (ancestors first). A trailing separator is ignored (<c>C:\src\</c> and <c>C:\src</c> are one
    /// entry); the root form <c>C:\</c> keeps its separator.</summary>
    public int GetOrAdd(string directoryPath)
    {
        directoryPath = Path.TrimEndingDirectorySeparator(directoryPath);
        if (_indexByPath.TryGetValue(directoryPath, out int existing))
            return existing;

        string? parent = Path.GetDirectoryName(directoryPath);
        int parentIndex;
        string name;
        if (string.IsNullOrEmpty(parent))
        {
            parentIndex = -1;          // a drive or UNC root — stored whole
            name = directoryPath;
        }
        else
        {
            parentIndex = GetOrAdd(parent);
            name = Path.GetFileName(directoryPath);
        }

        int index = _entries.Count;
        _entries.Add(new DryRunDirectory(name, parentIndex));
        _paths.Add(directoryPath);
        _indexByPath[directoryPath] = index;
        return index;
    }

    /// <summary>Splits an absolute file path plus its root into the wire triple.</summary>
    public (int DirIndex, string FileName, int RootDirIndex) Convert(string absolutePath, string root)
    {
        string? dir = Path.GetDirectoryName(absolutePath);
        if (string.IsNullOrEmpty(dir))
            throw new ArgumentException($"'{absolutePath}' is not an absolute file path", nameof(absolutePath));
        return (GetOrAdd(dir), Path.GetFileName(absolutePath), GetOrAdd(root));
    }

    public DryRunFile Convert(IPhysicalFileView file)
    {
        (int dirIndex, string fileName, int rootDirIndex) = Convert(file.Path, file.Root);
        return new DryRunFile
        {
            DirIndex = dirIndex,
            FileName = fileName,
            RootDirIndex = rootDirIndex,
            Length = file.Length,
            LastWritten = file.LastWritten,
            IsReparsePoint = file.IsReparsePoint,
        };
    }

    public DryRunOperation Convert(IFileOperationView op)
    {
        (int dirIndex, string fileName, int rootDirIndex) = Convert(op.Path, op.Root);
        return new DryRunOperation
        {
            DirIndex = dirIndex,
            FileName = fileName,
            RootDirIndex = rootDirIndex,
            Kind = op.Kind,
            SourceIndex = op.SourceIndex,
            SubjectIndex = op.SubjectIndex,
            SourceDisposition = op.SourceDisposition,
            Detail = op.Detail,
        };
    }

    /// <summary>The entries added since the last flush — the <c>Directories</c> slice for an
    /// outgoing chunk. Chunks carry each entry exactly once, in global index order, before the
    /// files/ops that reference it.</summary>
    public IReadOnlyList<DryRunDirectory> FlushNew()
    {
        List<DryRunDirectory> fresh = _entries.GetRange(_flushed, _entries.Count - _flushed);
        _flushed = _entries.Count;
        return fresh;
    }

    /// <summary>A rollback point for <see cref="RollbackTo"/>.</summary>
    public int Mark() => _entries.Count;

    /// <summary>Removes every entry added after <paramref name="mark"/> — the batched builder backs
    /// a rejected bundle's directories out when its byte-budget reservation fails. Not valid once
    /// the tail has been flushed to a chunk (it is already on the wire).</summary>
    public void RollbackTo(int mark)
    {
        if (mark < _flushed)
            throw new InvalidOperationException($"cannot roll back to {mark}: entries up to {_flushed} were already flushed");
        for (int i = _entries.Count - 1; i >= mark; i--)
            _indexByPath.Remove(_paths[i]);
        _entries.RemoveRange(mark, _entries.Count - mark);
        _paths.RemoveRange(mark, _paths.Count - mark);
    }
}

/// <summary>Consumer-side helpers over a report's directory table.</summary>
public static class DryRunDirectoryTable
{
    /// <summary>Materializes each entry's absolute directory path. The parents-precede-children
    /// invariant makes this a single forward pass; a violated invariant (malformed producer) throws
    /// rather than silently mis-rooting paths.</summary>
    public static string[] Materialize(IReadOnlyList<DryRunDirectory> directories)
    {
        ArgumentNullException.ThrowIfNull(directories);
        string[] paths = new string[directories.Count];
        for (int i = 0; i < directories.Count; i++)
        {
            DryRunDirectory entry = directories[i];
            if (entry.ParentIndex < -1 || entry.ParentIndex >= i)
                throw new InvalidOperationException(
                    $"malformed directory table: entry {i} ('{entry.Name}') has ParentIndex {entry.ParentIndex}");
            paths[i] = entry.ParentIndex < 0 ? entry.Name : Path.Join(paths[entry.ParentIndex], entry.Name);
        }
        return paths;
    }

    /// <summary>Returns a description of the first file/op record whose <c>DirIndex</c> or
    /// <c>RootDirIndex</c> falls outside a directory table of <paramref name="directoryCount"/>
    /// entries, or <c>null</c> when every reference is in range. Consumers index the materialized
    /// path array by these values, so an out-of-range reference from a corrupt or buggy producer
    /// must be rejected at the trust boundary rather than crashing deep in a consumer (e.g.
    /// <c>DryRunViewModel.ApplyReport</c>). The <c>(uint)</c> casts fold the negative and
    /// too-large checks into one comparison.</summary>
    public static string? FindInvalidReference(
        int directoryCount,
        IReadOnlyList<DryRunFile> files,
        IReadOnlyList<DryRunOperation> operations)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(operations);
        foreach (DryRunFile f in files)
        {
            if ((uint)f.DirIndex >= (uint)directoryCount)
                return $"file '{f.FileName}' DirIndex {f.DirIndex}";
            if ((uint)f.RootDirIndex >= (uint)directoryCount)
                return $"file '{f.FileName}' RootDirIndex {f.RootDirIndex}";
        }
        foreach (DryRunOperation o in operations)
        {
            if ((uint)o.DirIndex >= (uint)directoryCount)
                return $"operation '{o.FileName}' DirIndex {o.DirIndex}";
            if ((uint)o.RootDirIndex >= (uint)directoryCount)
                return $"operation '{o.FileName}' RootDirIndex {o.RootDirIndex}";
        }
        return null;
    }
}
