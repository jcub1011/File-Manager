using FileManager.Contracts.Primitives;
using FileManager.Core.Audit;
using FileManager.Core.Platform;
using System.Collections.Concurrent;

namespace FileManager.Core.Tests.TestSupport;

/// <summary>A <see cref="ITrashService"/> that moves into a real fake bin directory — so a test can
/// assert the file <b>survived</b> recycling rather than only that the call happened, which is the
/// whole difference between "recycled" and "destroyed" — plus two injection points.
/// <para><see cref="OnMove"/> runs at the instant of the move, which is how a test observes ORDERING
/// against what is on disk: under <c>MirrorDeletion.Proactive</c> the incoming copy must not exist yet
/// when an orphan is recycled, and under <c>AfterCopy</c> it must.</para></summary>
internal sealed class FaultyTrashService(string bin) : ITrashService
{
    /// <summary>Paths whose move fails, keyed case-insensitively like the filesystem.</summary>
    public HashSet<string> FailOnPaths { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Invoked with the path BEFORE it is moved, while it is still on disk.</summary>
    public Action<string>? OnMove { get; set; }

    public ConcurrentBag<string> Trashed { get; } = [];

    public Result MoveToTrash(string absolutePath)
    {
        OnMove?.Invoke(absolutePath);
        if (FailOnPaths.Contains(absolutePath))
            return Result.Failure($"injected trash failure for {absolutePath}");
        Trashed.Add(absolutePath);
        Directory.CreateDirectory(bin);
        // Name-collision-proof: two orphans can share a leaf name under different subdirectories, and
        // a real Recycle Bin keeps both.
        string destination = Path.Combine(bin, $"{Guid.NewGuid():N}-{Path.GetFileName(absolutePath)}");
        File.Move(absolutePath, destination, overwrite: true);
        return Result.Success();
    }
}

/// <summary>Fails the audit append for chosen paths, so a test can drive the one case where the file
/// is already gone but the no-loss trail could not be written — which must be reported as a FAILED
/// deletion, never as a clean one.</summary>
internal sealed class FaultyReconcileAuditLog(IReconcileAuditLog inner) : IReconcileAuditLog
{
    public HashSet<string> FailOnPaths { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Result Append(MirrorDeletionAuditRecord record) =>
        FailOnPaths.Contains(record.DestinationPath)
            ? Result.Failure($"injected audit failure for {record.DestinationPath}")
            : inner.Append(record);

    public Result<IReadOnlyList<MirrorDeletionAuditRecord>, string> ReadRecent(int count) =>
        inner.ReadRecent(count);
}
