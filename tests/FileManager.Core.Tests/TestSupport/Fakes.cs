using System.Collections.Concurrent;
using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using FileManager.Contracts.Settings;
using FileManager.Core.Platform;
using FileManager.Core.Settings;

namespace FileManager.Core.Tests.TestSupport;

/// <summary>Serves a fixed <see cref="GlobalSettings"/> snapshot (default unless one is supplied);
/// <see cref="Update"/> echoes the input.</summary>
internal sealed class FakeSettingsProvider(GlobalSettings? current = null) : ISettingsProvider
{
    public GlobalSettings Current { get; } = current ?? GlobalSettings.Default;
    public Result<GlobalSettings, string> Update(GlobalSettings settings) =>
        Result<GlobalSettings, string>.Success(settings);
}

/// <summary>Reports abundant free space and a per-drive-root volume key; lets a test force a
/// shortfall via <see cref="Free"/>.</summary>
internal sealed class FakeVolumeInfoProvider : IVolumeInfoProvider
{
    public long Free { get; set; } = long.MaxValue / 2;
    public bool Network { get; set; }

    public Result<long, string> GetAvailableFreeBytes(string path) => Free;

    public Result<string, string> GetVolumeKey(string path)
    {
        string root = Path.GetPathRoot(Path.GetFullPath(path)) ?? path;
        return Result<string, string>.Success(root.ToLowerInvariant());
    }

    public bool IsNetworkPath(string path) => Network;

    public Result<VolumeCapacity, string> GetVolumeCapacity(string path) =>
        new VolumeCapacity(long.MaxValue / 2, Free, 1);
}

/// <summary>No-op metadata preservation; a test can make <see cref="Apply"/> fail to exercise the
/// FailJob path.</summary>
internal sealed class FakeMetadataPreserver : IMetadataPreserver
{
    public bool FailApply { get; set; }

    public Result<MetadataLossReport, string> Inspect(string sourcePath, string targetDirectory) =>
        new MetadataLossReport(false, []);

    public Result Apply(string fromPath, string toPath, MetadataOnConflict onConflict) =>
        FailApply ? Result.Failure("forced metadata failure") : Result.Success();
}

/// <summary>Records trashed paths and (optionally) moves them into a fake bin directory.</summary>
internal sealed class FakeTrashService : ITrashService
{
    private readonly string? _bin;
    public ConcurrentBag<string> Trashed { get; } = [];

    public FakeTrashService(string? bin = null) => _bin = bin;

    public Result MoveToTrash(string absolutePath)
    {
        Trashed.Add(absolutePath);
        if (_bin is not null)
        {
            Directory.CreateDirectory(_bin);
            File.Move(absolutePath, Path.Combine(_bin, Path.GetFileName(absolutePath)), overwrite: true);
        }
        else
        {
            File.Delete(absolutePath);
        }
        return Result.Success();
    }
}
