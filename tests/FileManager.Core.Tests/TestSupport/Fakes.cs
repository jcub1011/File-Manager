using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FileManager.Contracts.IPC;
using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using FileManager.Contracts.Settings;
using FileManager.Core.Files;
using FileManager.Core.Jobs;
using FileManager.Core.Observability;
using FileManager.Core.Platform;
using FileManager.Core.Profiles;
using FileManager.Core.Settings;
using FileManager.Core.Watching;

namespace FileManager.Core.Tests.TestSupport;

/// <summary>Collects everything published, so a test can assert on the event stream a subsystem
/// produces without standing up the IPC server.</summary>
internal sealed class RecordingEventBus : IEngineEventBus
{
    private readonly List<EngineEvent> _events = [];

    public IReadOnlyList<EngineEvent> Events
    {
        get { lock (_events) return _events.ToList(); }
    }

    public void Publish(EngineEvent evt)
    {
        lock (_events) _events.Add(evt);
    }

    public IDisposable Subscribe(Action<EngineEvent> handler) => new Noop();

    private sealed class Noop : IDisposable { public void Dispose() { } }
}

/// <summary>Replays a scripted sequence of payloads and faults, so the folder-run path can be tested
/// without a real filesystem walk.</summary>
internal sealed class FakeSourceScanner(params Result<Payload, EnumerationFault>[] results) : ISourceScanner
{
    public List<string?> ScopeRoots { get; } = [];

    public IEnumerable<Result<Payload, EnumerationFault>> Scan(
        Profile profile, TriggerKind trigger, string? scopeRoot = null, CancellationToken ct = default)
    {
        ScopeRoots.Add(scopeRoot);
        return results;
    }
}

/// <summary>In-memory pause flag that notifies subscribers synchronously; lets a test toggle the
/// trigger-queue gate without touching disk.</summary>
internal sealed class FakePauseState : IPauseStateService
{
    private readonly List<Action<bool>> _handlers = [];
    public bool IsPaused { get; private set; }

    public Result SetPaused(bool paused)
    {
        IsPaused = paused;
        foreach (Action<bool> handler in _handlers.ToArray())
            handler(paused);
        return Result.Success();
    }

    public IDisposable Subscribe(Action<bool> pauseHandler)
    {
        _handlers.Add(pauseHandler);
        return new Subscription(_handlers, pauseHandler);
    }

    private sealed class Subscription(List<Action<bool>> handlers, Action<bool> handler) : IDisposable
    {
        public void Dispose() => handlers.Remove(handler);
    }
}

/// <summary>A catalog over a fixed profile set.</summary>
internal sealed class FakeProfileCatalog(params Profile[] profiles) : IProfileCatalog
{
    public IReadOnlyList<Profile> All { get; } = profiles;
    public IReadOnlyList<Profile> Active { get; } = profiles.Where(p => p.Active).ToList();
    public IDisposable Subscribe(Action changeHandler) => new Noop();
    public Result Reload() => Result.Success();
    private sealed class Noop : IDisposable { public void Dispose() { } }
}

/// <summary>Records executed plans and returns a canned completion (Succeeded unless overridden).</summary>
internal sealed class FakeJobExecutor : IJobExecutor
{
    public Func<JobPlan, JobCompletion>? OnExecute { get; set; }
    public ConcurrentBag<JobPlan> Executed { get; } = [];

    /// <summary>Invoked with the caller's progress sink before the completion is returned, so a test
    /// can drive progress frames through the orchestrator's publisher without a real executor.</summary>
    public Action<IProgress<JobProgress>?>? OnProgress { get; set; }

    public Task<JobCompletion> ExecuteAsync(
        JobPlan plan, IProgress<JobProgress>? progress = null, CancellationToken ct = default)
    {
        Executed.Add(plan);
        OnProgress?.Invoke(progress);
        JobCompletion completion = OnExecute?.Invoke(plan)
            ?? new JobCompletion(plan.JobId, JobOutcome.Succeeded, null, null, TimeSpan.Zero);
        return Task.FromResult(completion);
    }
}

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

    public DriveClass GetDriveClass(string path) => Network ? DriveClass.Network : DriveClass.Fixed;

    public Result<VolumeCapacity, string> GetVolumeCapacity(string path) =>
        new VolumeCapacity(long.MaxValue / 2, Free, 1);
}

/// <summary>No-op metadata preservation; <see cref="FailApply"/> simulates an ACL-preservation fault.
/// <para>By default it honours <see cref="MetadataOnConflict"/> exactly as
/// <c>WindowsMetadataPreserver</c> does — a fault is reported as a failure only under
/// <see cref="MetadataOnConflict.FailJob"/>, and swallowed as best-effort otherwise. Ignoring the policy
/// by default would let a regression that made best-effort metadata fatal pass every test.</para>
/// <para><see cref="PolicyBlind"/> drops that mirroring so a test can reach the placer's OWN handling of
/// a reported failure. Without it, no test could observe what the placer does under WarnAndContinue,
/// because the fake resolved the policy before the placer ever saw an error.</para></summary>
internal sealed class FakeMetadataPreserver : IMetadataPreserver
{
    public bool FailApply { get; set; }

    /// <summary>Report a failure regardless of the policy — the lever for testing the placer's own
    /// policy handling rather than the preserver's.</summary>
    public bool PolicyBlind { get; set; }

    /// <summary>Every Apply call, so a test can prove metadata was attempted.</summary>
    public ConcurrentBag<(string From, string To, MetadataOnConflict OnConflict)> Applied { get; } = [];

    public Result<MetadataLossReport, string> Inspect(string sourcePath, string targetDirectory) =>
        new MetadataLossReport(false, []);

    public Result Apply(string fromPath, string toPath, MetadataOnConflict onConflict)
    {
        Applied.Add((fromPath, toPath, onConflict));
        if (!FailApply)
            return Result.Success();
        return PolicyBlind || onConflict == MetadataOnConflict.FailJob
            ? Result.Failure("forced metadata failure")
            : Result.Success();
    }
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
