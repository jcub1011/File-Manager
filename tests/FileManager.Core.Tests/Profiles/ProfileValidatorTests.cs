using FileManager.Contracts.IPC;
using FileManager.Contracts.Profiles;
using FileManager.Core.Filtering;
using FileManager.Core.Profiles;
using FileManager.Core.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileManager.Core.Tests.Profiles;

/// <summary>One mutation per §4.1 validation code, asserting code and severity.</summary>
public sealed class ProfileValidatorTests
{
    private static ProfileValidator NewValidator() => new(
        NullLogger<ProfileValidator>.Instance,
        new FilterCompiler(NullLogger<FilterCompiler>.Instance, TimeProvider.System));

    private static IReadOnlyList<ValidationIssue> Validate(Profile candidate, params Profile[] others) =>
        NewValidator().Validate(candidate, others);

    private static void AssertHas(
        IReadOnlyList<ValidationIssue> issues, string code, ValidationSeverity severity) =>
        Assert.Contains(issues, i => i.Code == code && i.Severity == severity);

    [Fact]
    public void Baseline_profile_is_clean() =>
        Assert.Empty(Validate(TestProfiles.Valid()));

    [Fact]
    public void Unknown_schema_version_is_an_error() =>
        AssertHas(Validate(TestProfiles.Valid() with { SchemaVersion = 1 }),
            "PROFILE_SCHEMA_VERSION", ValidationSeverity.Error);

    [Fact]
    public void Mirror_sync_mode_is_allowed_but_must_be_ACKNOWLEDGED()
    {
        // Mirror is not reserved — it is implemented — but it is the only mode that deletes files the
        // user never put in the source, so saving one costs a single explicit acknowledgment. A
        // BlockingWarning, not an Error: the profile is legal, the consequence just has to be read.
        IReadOnlyList<ValidationIssue> issues = Validate(TestProfiles.Valid() with { SyncMode = SyncMode.Mirror });

        ValidationIssue issue = Assert.Single(issues);
        Assert.Equal("PROFILE_MIRROR_DELETES", issue.Code);
        Assert.Equal(ValidationSeverity.BlockingWarning, issue.Severity);
        // The consequence that would otherwise be reported as data loss has to be in the words.
        Assert.Contains("filters", issue.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(issues, i => i.Severity == ValidationSeverity.Error);
    }

    [Fact]
    public void AdditiveArchive_needs_no_acknowledgment()
    {
        Assert.Empty(Validate(TestProfiles.Valid()));
    }

    [Fact]
    public void SizeTimestamp_verification_is_reserved()
    {
        Profile candidate = TestProfiles.Valid();
        candidate = candidate with
        {
            Policies = candidate.Policies with { VerificationMethod = VerificationMethod.SizeTimestamp },
        };
        AssertHas(Validate(candidate), "PROFILE_RESERVED_VALUE", ValidationSeverity.Error);
    }

    [Fact]
    public void ContentHashDedupe_is_reserved()
    {
        Profile candidate = TestProfiles.Valid() with { Filters = new FilterSet { ContentHashDedupe = true } };
        AssertHas(Validate(candidate), "PROFILE_RESERVED_VALUE", ValidationSeverity.Error);
    }

    [Fact]
    public void Shell_argument_mode_is_reserved()
    {
        string executable = Path.GetTempFileName();
        try
        {
            Profile candidate = TestProfiles.Valid() with
            {
                Transformers =
                [
                    new TransformerStep
                    {
                        Step = 1, Name = "t", ExecutablePath = executable,
                        ArgumentMode = ArgumentMode.Shell, Arguments = "",
                        OutputMode = OutputMode.InPlace, TimeoutSeconds = 5,
                    },
                ],
            };
            AssertHas(Validate(candidate), "PROFILE_RESERVED_VALUE", ValidationSeverity.Error);
        }
        finally
        {
            File.Delete(executable);
        }
    }

    [Fact]
    public void Relative_source_path_is_invalid()
    {
        Profile candidate = TestProfiles.Valid(sourcePath: @"relative\path");
        AssertHas(Validate(candidate), "PROFILE_PATH_INVALID", ValidationSeverity.Error);
    }

    [Fact]
    public void Infrastructure_directory_in_target_path_is_invalid()
    {
        Profile candidate = TestProfiles.Valid(targetPath: @"C:\fm-test\.fm_staging\dst");
        AssertHas(Validate(candidate), "PROFILE_PATH_INVALID", ValidationSeverity.Error);
    }

    [Fact]
    public void Target_equal_to_source_is_an_error()
    {
        // Different casing on purpose: path identity is OrdinalIgnoreCase on Windows.
        Profile candidate = TestProfiles.Valid(
            sourcePath: @"C:\fm-test\same", targetPath: @"C:\FM-TEST\SAME");
        AssertHas(Validate(candidate), "PROFILE_TARGET_EQUALS_SOURCE", ValidationSeverity.Error);
    }

    /// <summary>Two Targets naming one root give every job two TargetPlans with an identical final path.
    /// Nothing separates them at placement time — the path locks are per-job — so the two parallel
    /// placements race the same temp→final move and the job rolls back on every attempt, meaning the file
    /// is never delivered. Refused at save time, with the same OrdinalIgnoreCase identity as
    /// target-equals-source.</summary>
    [Fact]
    public void Duplicate_targets_are_an_error()
    {
        Profile candidate = TestProfiles.Valid() with
        {
            Targets =
            [
                new TargetConfig { Path = @"C:\fm-test\out" },
                new TargetConfig { Path = @"C:\FM-TEST\OUT" },
            ],
        };
        AssertHas(Validate(candidate), "PROFILE_TARGET_DUPLICATE", ValidationSeverity.Error);
    }

    [Fact]
    public void Two_distinct_targets_are_fine()
    {
        Profile candidate = TestProfiles.Valid() with
        {
            Targets =
            [
                new TargetConfig { Path = @"C:\fm-test\out-a" },
                new TargetConfig { Path = @"C:\fm-test\out-b" },
            ],
        };
        Assert.DoesNotContain(Validate(candidate), i => i.Code == "PROFILE_TARGET_DUPLICATE");
    }

    [Fact]
    public void Empty_sources_and_targets_are_errors()
    {
        Profile candidate = TestProfiles.Valid() with { Sources = [], Targets = [] };
        var issues = Validate(candidate);
        AssertHas(issues, "PROFILE_NO_SOURCES", ValidationSeverity.Error);
        AssertHas(issues, "PROFILE_NO_TARGETS", ValidationSeverity.Error);
    }

    [Fact]
    public void Transformer_structural_faults_are_errors()
    {
        string executable = Path.GetTempFileName();
        try
        {
            Profile candidate = TestProfiles.Valid() with
            {
                Transformers =
                [
                    // Non-contiguous steps, missing NewFile extension, missing exe, zero timeout.
                    new TransformerStep
                    {
                        Step = 1, Name = "a", ExecutablePath = executable,
                        ArgumentMode = ArgumentMode.Literal, Arguments = "",
                        OutputMode = OutputMode.NewFile, ExpectedOutputExtension = null, TimeoutSeconds = 5,
                    },
                    new TransformerStep
                    {
                        Step = 3, Name = "b", ExecutablePath = @"C:\no\such\exe.exe",
                        ArgumentMode = ArgumentMode.Literal, Arguments = "",
                        OutputMode = OutputMode.InPlace, TimeoutSeconds = 0,
                    },
                ],
            };
            var issues = Validate(candidate).Where(i => i.Code == "PROFILE_TRANSFORMER_INVALID").ToList();
            Assert.True(issues.Count >= 4, $"expected ≥4 transformer issues, got {issues.Count}");
            Assert.All(issues, i => Assert.Equal(ValidationSeverity.Error, i.Severity));
        }
        finally
        {
            File.Delete(executable);
        }
    }

    [Fact]
    public void Uncompilable_filter_pattern_is_an_error()
    {
        Profile candidate = TestProfiles.Valid() with
        {
            Filters = new FilterSet { IncludeRegex = ["[unclosed"] },
        };
        AssertHas(Validate(candidate), "PROFILE_FILTER_INVALID", ValidationSeverity.Error);
    }

    [Fact]
    public void Bad_cron_and_timezone_are_errors()
    {
        Profile candidate = TestProfiles.Valid() with
        {
            Triggers = new TriggerSettings
            {
                ManualShell = true,
                Watcher = false,
                Schedule = new ScheduleSettings
                {
                    Enabled = true, Cron = "99 * * *", Timezone = "Not/AZone",
                    MissedRunPolicy = MissedRunPolicy.Skip,
                },
            },
        };
        var issues = Validate(candidate);
        AssertHas(issues, "PROFILE_CRON_INVALID", ValidationSeverity.Error);
        AssertHas(issues, "PROFILE_TIMEZONE_INVALID", ValidationSeverity.Error);
    }

    [Fact]
    public void Valid_schedule_passes()
    {
        Profile candidate = TestProfiles.Valid() with
        {
            Triggers = new TriggerSettings
            {
                ManualShell = true,
                Watcher = false,
                Schedule = new ScheduleSettings
                {
                    Enabled = true, Cron = "*/15 2-6 * * 1-5", Timezone = "UTC",
                    MissedRunPolicy = MissedRunPolicy.CatchUpOnce,
                },
            },
        };
        Assert.Empty(Validate(candidate));
    }

    [Fact]
    public void Archive_action_without_folder_is_an_error()
    {
        Profile candidate = TestProfiles.Valid();
        candidate = candidate with
        {
            Policies = candidate.Policies with { OnSuccess = OnSuccessAction.MoveToArchive, ArchiveFolder = null },
        };
        AssertHas(Validate(candidate), "PROFILE_ARCHIVE_MISSING", ValidationSeverity.Error);
    }

    [Fact]
    public void Target_inside_own_source_warns()
    {
        Profile candidate = TestProfiles.Valid(
            sourcePath: @"C:\fm-test\source", targetPath: @"C:\fm-test\source\nested");
        AssertHas(Validate(candidate), "PROFILE_TARGET_IN_SOURCE_WARN", ValidationSeverity.Warning);
    }

    [Fact]
    public void Target_feeding_another_active_profile_warns()
    {
        Profile candidate = TestProfiles.Valid(targetPath: @"C:\fm-test\handoff");
        Profile other = TestProfiles.Valid(sourcePath: @"C:\fm-test\handoff", targetPath: @"C:\fm-test\elsewhere");
        AssertHas(Validate(candidate, other), "PROFILE_TARGET_IN_SOURCE_WARN", ValidationSeverity.Warning);
    }

    [Fact]
    public void Two_profile_cycle_warns()
    {
        Profile candidate = TestProfiles.Valid(sourcePath: @"C:\fm-test\a", targetPath: @"C:\fm-test\b");
        Profile other = TestProfiles.Valid(sourcePath: @"C:\fm-test\b", targetPath: @"C:\fm-test\a");
        AssertHas(Validate(candidate, other), "PROFILE_CYCLE_WARN", ValidationSeverity.Warning);
    }

    [Fact]
    public void Overlapping_sources_with_disposal_warn()
    {
        Profile candidate = TestProfiles.Valid(sourcePath: @"C:\fm-test\shared");
        candidate = candidate with
        {
            Policies = candidate.Policies with { OnSuccess = OnSuccessAction.MoveToTrash },
        };
        Profile other = TestProfiles.Valid(
            sourcePath: @"C:\fm-test\shared\inner", targetPath: @"C:\fm-test\other-dst");
        AssertHas(Validate(candidate, other), "PROFILE_OVERLAP_DISPOSAL_WARN", ValidationSeverity.Warning);
    }

    [Fact]
    public void Unverified_permanent_delete_is_a_blocking_warning()
    {
        Profile candidate = TestProfiles.Valid();
        candidate = candidate with
        {
            Policies = candidate.Policies with
            {
                VerificationMethod = VerificationMethod.None,
                OnSuccess = OnSuccessAction.PermanentDelete,
            },
        };
        AssertHas(Validate(candidate), "PROFILE_UNVERIFIED_DELETE", ValidationSeverity.BlockingWarning);
    }

    // ---- LargeFileIdentity (spec §3.4.1 bounded-read identity) ----------------------------------

    private static Profile WithIdentity(
        LargeFileIdentity identity,
        OnSuccessAction onSuccess = OnSuccessAction.KeepSource,
        VerificationMethod verification = VerificationMethod.XxHash128,
        long threshold = 256L * 1024 * 1024)
    {
        Profile candidate = TestProfiles.Valid();
        return candidate with
        {
            Policies = candidate.Policies with
            {
                LargeFileIdentity = identity,
                OnSuccess = onSuccess,
                VerificationMethod = verification,
                LargeFileIdentityThresholdBytes = threshold,
            },
        };
    }

    [Fact]
    public void The_default_exact_identity_raises_nothing()
    {
        // The whole feature must be silent for anyone who does not opt in.
        Assert.Empty(Validate(WithIdentity(LargeFileIdentity.FullHash)));
    }

    [Theory]
    [InlineData(LargeFileIdentity.SampledHash)]
    [InlineData(LargeFileIdentity.TimestampOrSampledHash)]
    [InlineData(LargeFileIdentity.SizeAndTimestamp)]
    public void A_cheap_identity_with_permanent_delete_must_be_acknowledged(LargeFileIdentity identity)
    {
        IReadOnlyList<ValidationIssue> issues =
            Validate(WithIdentity(identity, OnSuccessAction.PermanentDelete));

        AssertHas(issues, "PROFILE_SAMPLED_IDENTITY_DELETE", ValidationSeverity.BlockingWarning);
        // The mechanism has to be in the words, not just the verdict: the danger is a target left on an
        // older version while the source is destroyed.
        ValidationIssue issue = Assert.Single(issues, i => i.Code == "PROFILE_SAMPLED_IDENTITY_DELETE");
        Assert.Contains("older version", issue.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(LargeFileIdentity.SampledHash)]
    [InlineData(LargeFileIdentity.TimestampOrSampledHash)]
    public void A_cheap_identity_with_move_to_trash_only_warns(LargeFileIdentity identity)
    {
        IReadOnlyList<ValidationIssue> issues = Validate(WithIdentity(identity, OnSuccessAction.MoveToTrash));

        AssertHas(issues, "PROFILE_SAMPLED_IDENTITY_TRASH_WARN", ValidationSeverity.Warning);
        Assert.DoesNotContain(issues, i => i.Severity is ValidationSeverity.Error or ValidationSeverity.BlockingWarning);
    }

    [Fact]
    public void A_cheap_identity_that_keeps_the_source_is_not_blocked()
    {
        // Nothing is destroyed, so a stale destination is recoverable by re-running under FullHash.
        IReadOnlyList<ValidationIssue> issues = Validate(WithIdentity(LargeFileIdentity.SampledHash));
        Assert.Empty(issues);
    }

    [Fact]
    public void Metadata_only_identity_warns_that_it_reads_nothing()
    {
        IReadOnlyList<ValidationIssue> issues = Validate(WithIdentity(LargeFileIdentity.SizeAndTimestamp));
        AssertHas(issues, "PROFILE_METADATA_IDENTITY_WARN", ValidationSeverity.Warning);
    }

    [Fact]
    public void A_cheap_identity_is_reported_as_inert_when_verification_does_not_hash()
    {
        // Silently ignoring a setting the user deliberately chose is worse than saying it does nothing.
        IReadOnlyList<ValidationIssue> issues =
            Validate(WithIdentity(LargeFileIdentity.SampledHash, verification: VerificationMethod.None));

        AssertHas(issues, "PROFILE_IDENTITY_WITHOUT_HASH_WARN", ValidationSeverity.Warning);
        // ...and it must not also claim the sampled-identity risk, which cannot arise here.
        Assert.DoesNotContain(issues, i => i.Code == "PROFILE_METADATA_IDENTITY_WARN");
        Assert.DoesNotContain(issues, i => i.Code == "PROFILE_SAMPLED_IDENTITY_DELETE");
    }

    [Fact]
    public void A_negative_identity_threshold_is_an_error() =>
        AssertHas(Validate(WithIdentity(LargeFileIdentity.FullHash, threshold: -1)),
            "PROFILE_IDENTITY_THRESHOLD_INVALID", ValidationSeverity.Error);

    [Fact]
    public void Unverified_trash_warns()
    {
        Profile candidate = TestProfiles.Valid();
        candidate = candidate with
        {
            Policies = candidate.Policies with
            {
                VerificationMethod = VerificationMethod.None,
                OnSuccess = OnSuccessAction.MoveToTrash,
            },
        };
        AssertHas(Validate(candidate), "PROFILE_UNVERIFIED_TRASH_WARN", ValidationSeverity.Warning);
    }

    private static Profile WithDestructiveSource(string sourcePath, OnSuccessAction onSuccess = OnSuccessAction.PermanentDelete)
    {
        Profile candidate = TestProfiles.Valid(sourcePath: sourcePath);
        return candidate with { Policies = candidate.Policies with { OnSuccess = onSuccess } };
    }

    [Fact]
    public void Destructive_disposition_on_the_user_profile_root_is_a_blocking_warning() =>
        AssertHas(Validate(WithDestructiveSource(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))),
            "PROFILE_HIGH_RISK_SOURCE", ValidationSeverity.BlockingWarning);

    [Fact]
    public void Destructive_disposition_on_a_drive_root_is_a_blocking_warning() =>
        AssertHas(Validate(WithDestructiveSource(Path.GetPathRoot(Path.GetTempPath())!)),
            "PROFILE_HIGH_RISK_SOURCE", ValidationSeverity.BlockingWarning);

    [Fact]
    public void Destructive_disposition_under_the_windows_directory_is_a_blocking_warning() =>
        AssertHas(Validate(WithDestructiveSource(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp"),
                OnSuccessAction.MoveToTrash)),
            "PROFILE_HIGH_RISK_SOURCE", ValidationSeverity.BlockingWarning);

    [Fact]
    public void Destructive_disposition_on_a_normal_subfolder_is_not_flagged()
    {
        // Downloads-style subfolders of the user profile are the tool's bread and butter.
        string downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        Assert.DoesNotContain(Validate(WithDestructiveSource(downloads)),
            i => i.Code == "PROFILE_HIGH_RISK_SOURCE");
    }

    [Fact]
    public void Keeping_sources_on_a_drive_root_is_not_flagged()
    {
        Profile candidate = TestProfiles.Valid(sourcePath: Path.GetPathRoot(Path.GetTempPath())!);
        Assert.DoesNotContain(Validate(candidate), i => i.Code == "PROFILE_HIGH_RISK_SOURCE");
    }

    [Fact]
    public void Transformer_steps_warn_with_the_executable_named()
    {
        string executable = Path.GetTempFileName();
        try
        {
            Profile candidate = TestProfiles.Valid() with
            {
                Transformers =
                [
                    new TransformerStep
                    {
                        Step = 1, Name = "t", ExecutablePath = executable,
                        ArgumentMode = ArgumentMode.Literal, Arguments = "$input",
                        OutputMode = OutputMode.InPlace, TimeoutSeconds = 5,
                    },
                ],
            };
            IReadOnlyList<ValidationIssue> issues = Validate(candidate);
            ValidationIssue warn = Assert.Single(issues, i => i.Code == "PROFILE_TRANSFORMER_EXECUTABLE_WARN");
            Assert.Equal(ValidationSeverity.Warning, warn.Severity);
            Assert.Contains(executable, warn.Message);
        }
        finally
        {
            File.Delete(executable);
        }
    }

    [Fact]
    public void An_exported_profile_round_trips_through_json_and_passes_the_real_validator()
    {
        // The UI import/export tests run against a fake gateway that never validates; this pins the
        // wire shape (FileManagerJsonContext, the exporter's serializer) to the real validator so a
        // schema-version or shape drift fails HERE instead of green-lighting a doomed import.
        Profile original = TestProfiles.Valid();
        string json = System.Text.Json.JsonSerializer.Serialize(
            original, FileManager.Contracts.FileManagerJsonContext.Default.Profile);
        Profile? imported = System.Text.Json.JsonSerializer.Deserialize(
            json, FileManager.Contracts.FileManagerJsonContext.Default.Profile);

        Assert.NotNull(imported);
        Assert.Equal(ProfileValidator.SupportedSchemaVersion, imported!.SchemaVersion);
        Assert.DoesNotContain(Validate(imported), i => i.Severity == ValidationSeverity.Error);
    }
}
