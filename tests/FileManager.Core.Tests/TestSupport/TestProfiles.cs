using FileManager.Contracts.Profiles;

namespace FileManager.Core.Tests.TestSupport;

/// <summary>A TimeProvider pinned to one instant.</summary>
internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

/// <summary>A valid baseline profile; tests mutate one field per validation code.</summary>
internal static class TestProfiles
{
    public static Profile Valid(
        string sourcePath = @"C:\fm-test\source",
        string targetPath = @"C:\fm-test\target") => new()
    {
        SchemaVersion = 2,
        Id = Guid.NewGuid(),
        Name = "Baseline",
        Active = true,
        SyncMode = SyncMode.AdditiveArchive,
        TargetLayout = TargetLayout.PreserveStructure,
        Triggers = new TriggerSettings { ManualShell = true, Watcher = false, Schedule = null },
        Sources = [new SourceConfig { Path = sourcePath }],
        Targets = [new TargetConfig { Path = targetPath }],
        Policies = DefaultPolicies(),
        Filters = null,
        Logging = new LoggingSettings { Verbosity = LogVerbosity.FailuresAndSkips, NotifyOnFailure = true },
    };

    public static PolicySettings DefaultPolicies() => new()
    {
        ConflictResolution = ConflictResolution.Skip,
        OverwriteHandling = OverwriteHandling.StageOverwrites,
        VerificationMethod = VerificationMethod.Sha256,
        OnSuccess = OnSuccessAction.KeepSource,
        ArchiveFolder = null,
        OnFailure = OnFailureAction.AbortRestoreAndClean,
        MetadataOnConflict = MetadataOnConflict.WarnAndContinue,
    };
}
