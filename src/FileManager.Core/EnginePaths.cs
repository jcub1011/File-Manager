using System;
using System.IO;

namespace FileManager.Core;

/// <summary>The engine's on-disk root (§9). Injected so tests can point everything at a temp
/// directory. Default: %LOCALAPPDATA%\FileManager\ (per-user, no elevation — spec §5.3).</summary>
public sealed record EnginePaths
{
    public required string Root { get; init; }

    public string ProfilesDirectory => Path.Combine(Root, "profiles");

    /// <summary>Rotating logs live here (spec §9): service-YYYYMMDD.log, 14-day retention.</summary>
    public string LogsDirectory => Path.Combine(Root, "logs");

    /// <summary>Per-job drill-down logs (spec §9): logs/jobs/&lt;job-id&gt;.log, most recent 500 kept.</summary>
    public string JobLogsDirectory => Path.Combine(LogsDirectory, "jobs");

    /// <summary>Append-only, fsync'd write-ahead journal segments (§5.5, §9):
    /// journal-000001.ndjsonl, rotated at EngineConfig.JournalRotateAtBytes.</summary>
    public string JournalDirectory => Path.Combine(Root, "journal");

    /// <summary>Deletion audit trail (spec §7, §9): audit-YYYYMM.ndjsonl, never auto-deleted.</summary>
    public string AuditDirectory => Path.Combine(Root, "audit");

    /// <summary>Persisted trigger state (§9): pause.json, schedule.json.</summary>
    public string StateDirectory => Path.Combine(Root, "state");

    /// <summary>Default profile temp root (§9): holds .pipeline_tmp/&lt;job-id&gt;/ transformer
    /// workspaces. Overridable per-engine via EngineConfig.TempRoot.</summary>
    public string WorkDirectory => Path.Combine(Root, "work");

    /// <summary>Orphaned staging content parked by crash recovery (§7.3, §9) — user data, never
    /// auto-deleted: quarantine/&lt;job-id&gt;/.</summary>
    public string QuarantineDirectory => Path.Combine(Root, "quarantine");

    /// <summary>Where a large dry run spills its findings before streaming them (§9). Root-relative like
    /// every other directory here, so an injected Root actually covers it — <c>GlobalSettings</c>'s own
    /// default resolves against the process-global %LOCALAPPDATA%, which a test cannot move.</summary>
    public string ScratchDirectory => Path.Combine(Root, "scratch");

    /// <summary>Frozen work lists for live runs: runs/&lt;run-id&gt;/{plan.json, items.ndjsonl}. A run
    /// captures what it is going to do BEFORE it does any of it, then executes from that capture, so
    /// the work cannot drift with the filesystem underneath it and the user can approve an itemized
    /// list rather than an intention. Deleted when a run closes; a directory left behind belongs to a
    /// run that died and is swept at startup.</summary>
    public string RunsDirectory => Path.Combine(Root, "runs");

    /// <summary>Machine-level settings (§9): a single settings.json at the root.</summary>
    public string SettingsFilePath => Path.Combine(Root, "settings.json");

    public static EnginePaths Default() => new()
    {
        Root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FileManager"),
    };
}
