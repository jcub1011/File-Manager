using FileManager.Contracts.Profiles;

namespace FileManager.UI.Tests.TestData;

internal static class ProfileFactory
{
    public static Profile Sample(Guid? id = null) => new()
    {
        SchemaVersion = 2,
        Id = id ?? Guid.NewGuid(),
        Name = "Existing",
        Active = true,
        SyncMode = SyncMode.AdditiveArchive,
        TargetLayout = TargetLayout.Flatten,
        Triggers = new TriggerSettings
        {
            ManualShell = true,
            Watcher = true,   // deliberately not the new-profile default — round-trip must keep it
            Schedule = new ScheduleSettings
            {
                Enabled = true, Cron = "0 3 * * *", Timezone = "UTC",
                MissedRunPolicy = MissedRunPolicy.CatchUpOnce,
            },
        },
        Sources = [new SourceConfig { Path = @"C:\ui-test\src", SettleDelaySeconds = 7, StabilityIntervalMs = 900 }],
        Transformers =
        [
            new TransformerStep
            {
                Step = 1, Name = "convert", ExecutablePath = @"C:\tools\conv.exe",
                ArgumentMode = ArgumentMode.Literal, Arguments = "$input $output",
                OutputMode = OutputMode.NewFile, ExpectedOutputExtension = ".flac", TimeoutSeconds = 60,
            },
        ],
        Targets = [new TargetConfig { Path = @"C:\ui-test\dst" }],
        Policies = new PolicySettings
        {
            ConflictResolution = ConflictResolution.RenameSuffix,
            OverwriteHandling = OverwriteHandling.DirectOverwrite,
            VerificationMethod = VerificationMethod.None,
            OnSuccess = OnSuccessAction.MoveToArchive,
            ArchiveFolder = @"C:\ui-test\archive",
            OnFailure = OnFailureAction.AbortRestoreAndClean,
            MetadataOnConflict = MetadataOnConflict.FailJob,
        },
        Filters = new FilterSet
        {
            Include = ["*.wav", "*.flac"],
            ExcludeGlob = ["*.tmp"],
            IncludeRegex = ["^keep-"],       // not surfaced by the editor — must round-trip
            MinSizeBytes = 100,
            MaxDepth = 3,
        },
        Logging = new LoggingSettings { Verbosity = LogVerbosity.All, NotifyOnFailure = false },
    };
}
