using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using FileManager.Core.Jobs;
using FileManager.Core.Tests.TestSupport;

namespace FileManager.Core.Tests.Jobs;

/// <summary>Where every file lands. The factory computes each target's prospective final path from the
/// profile's layout and the payload's source root, and everything downstream — conflict resolution,
/// the lock set, placement, the unchanged-check — is keyed off that path. A mistake here silently
/// misfiles content rather than failing, so the path arithmetic is pinned exhaustively.</summary>
public sealed class JobPlanFactoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fm-planfactory-" + Guid.NewGuid().ToString("N"));
    private readonly string _sourceRoot;
    private readonly JobPlanFactory _factory;

    public JobPlanFactoryTests()
    {
        _sourceRoot = Path.Combine(_root, "source");
        Directory.CreateDirectory(_sourceRoot);
        _factory = new JobPlanFactory(new EnginePaths { Root = Path.Combine(_root, "engine") }, new EngineConfig());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private string WriteSource(string relativePath, string content = "payload")
    {
        string path = Path.Combine(_sourceRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private Profile Profile(
        TargetLayout layout = TargetLayout.PreserveStructure,
        IReadOnlyList<string>? sourceRoots = null,
        IReadOnlyList<string>? targetRoots = null)
    {
        Profile baseline = TestProfiles.Valid(_sourceRoot, Path.Combine(_root, "target"));
        return baseline with
        {
            TargetLayout = layout,
            Sources = [.. (sourceRoots ?? [_sourceRoot]).Select(p => new SourceConfig { Path = p })],
            Targets = [.. (targetRoots ?? [Path.Combine(_root, "target")]).Select(p => new TargetConfig { Path = p })],
        };
    }

    private Payload PayloadFor(string sourcePath, string? sourceRoot = null) =>
        new(Guid.NewGuid(), sourcePath, sourceRoot ?? _sourceRoot, TriggerKind.ManualShell, DateTimeOffset.UnixEpoch);

    private JobPlan Build(Profile profile, Payload payload)
    {
        Result<JobPlan, JobError> result = _factory.Build(profile, payload);
        Assert.True(result.TryGetValue(out JobPlan? plan), $"Build failed: {Error(result)}");
        return plan!;
    }

    private static string Error(Result<JobPlan, JobError> result) =>
        result.TryGetError(out JobError? e) ? e.Message : "(none)";

    // ---- layout arithmetic -----------------------------------------------------------------------

    [Fact]
    public void PreserveStructure_mirrors_the_path_relative_to_the_source_root()
    {
        string source = WriteSource(Path.Combine("2026", "07", "photo.jpg"));
        string targetRoot = Path.Combine(_root, "target");

        JobPlan plan = Build(Profile(TargetLayout.PreserveStructure), PayloadFor(source));

        TargetPlan target = Assert.Single(plan.Targets);
        Assert.Equal(Path.Combine(targetRoot, "2026", "07", "photo.jpg"), target.ProspectiveFinalPath);
        Assert.Equal(targetRoot, target.TargetRoot);
        Assert.Equal(0, target.TargetIndex);
    }

    [Fact]
    public void Flatten_drops_the_relative_path_and_uses_the_bare_file_name()
    {
        string source = WriteSource(Path.Combine("2026", "07", "photo.jpg"));
        string targetRoot = Path.Combine(_root, "target");

        JobPlan plan = Build(Profile(TargetLayout.Flatten), PayloadFor(source));

        Assert.Equal(Path.Combine(targetRoot, "photo.jpg"), Assert.Single(plan.Targets).ProspectiveFinalPath);
    }

    [Fact]
    public void A_file_directly_in_the_source_root_lands_in_the_target_root_under_either_layout()
    {
        string source = WriteSource("top.txt");
        string targetRoot = Path.Combine(_root, "target");
        string expected = Path.Combine(targetRoot, "top.txt");

        Assert.Equal(expected, Assert.Single(Build(Profile(TargetLayout.PreserveStructure), PayloadFor(source)).Targets).ProspectiveFinalPath);
        Assert.Equal(expected, Assert.Single(Build(Profile(TargetLayout.Flatten), PayloadFor(source)).Targets).ProspectiveFinalPath);
    }

    [Fact]
    public void Multiple_sources_force_Flatten_even_when_the_profile_says_PreserveStructure()
    {
        // Spec §3.1.2: M:1 flattens, because two source trees' relative paths would collide
        // unpredictably under one target root.
        string otherRoot = Path.Combine(_root, "source-b");
        Directory.CreateDirectory(otherRoot);
        string source = WriteSource(Path.Combine("deep", "nested", "photo.jpg"));
        string targetRoot = Path.Combine(_root, "target");

        JobPlan plan = Build(
            Profile(TargetLayout.PreserveStructure, sourceRoots: [_sourceRoot, otherRoot]),
            PayloadFor(source));

        Assert.Equal(Path.Combine(targetRoot, "photo.jpg"), Assert.Single(plan.Targets).ProspectiveFinalPath);
    }

    [Fact]
    public void Every_target_root_gets_its_own_plan_entry_with_a_sequential_index()
    {
        string source = WriteSource(Path.Combine("sub", "photo.jpg"));
        string[] roots = [Path.Combine(_root, "t1"), Path.Combine(_root, "t2"), Path.Combine(_root, "t3")];

        JobPlan plan = Build(Profile(TargetLayout.PreserveStructure, targetRoots: roots), PayloadFor(source));

        Assert.Equal(3, plan.Targets.Count);
        for (int i = 0; i < roots.Length; i++)
        {
            Assert.Equal(i, plan.Targets[i].TargetIndex);
            Assert.Equal(roots[i], plan.Targets[i].TargetRoot);
            Assert.Equal(Path.Combine(roots[i], "sub", "photo.jpg"), plan.Targets[i].ProspectiveFinalPath);
        }
    }

    [Fact]
    public void A_source_outside_the_declared_root_does_not_escape_the_target_root_under_Flatten()
    {
        // A payload whose path is not under its declared root would make GetRelativePath produce
        // "..\..\x" — which, combined with the target root, would write OUTSIDE it. Flatten is immune
        // because it uses only the file name.
        string outside = Path.Combine(_root, "elsewhere", "stray.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
        File.WriteAllText(outside, "payload");
        string targetRoot = Path.Combine(_root, "target");

        JobPlan plan = Build(Profile(TargetLayout.Flatten), PayloadFor(outside));

        string final = Assert.Single(plan.Targets).ProspectiveFinalPath;
        Assert.Equal(Path.Combine(targetRoot, "stray.txt"), final);
        Assert.StartsWith(targetRoot, Path.GetFullPath(final), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Awkward_file_names_survive_path_composition()
    {
        string name = "café (1) [copy] $x.tar.gz";
        string source = WriteSource(name);

        JobPlan plan = Build(Profile(TargetLayout.Flatten), PayloadFor(source));

        Assert.Equal(name, Path.GetFileName(Assert.Single(plan.Targets).ProspectiveFinalPath));
    }

    // ---- snapshots the executor and recovery depend on -------------------------------------------

    [Fact]
    public void The_source_snapshot_records_the_size_and_last_write_time_on_disk()
    {
        // Recovery compares the filesystem against this snapshot, so it must reflect reality.
        string source = WriteSource("doc.txt", "twelve chars");
        var info = new FileInfo(source);

        JobPlan plan = Build(Profile(), PayloadFor(source));

        Assert.Equal(source, plan.Source.Path);
        Assert.Equal(info.Length, plan.Source.SizeBytes);
        Assert.Equal(info.LastWriteTimeUtc, plan.Source.LastWriteUtc.UtcDateTime);
        Assert.Equal(info.Length, plan.SourceMetadata.Length);
    }

    [Fact]
    public void The_policy_snapshot_copies_every_field_the_executor_reads()
    {
        // The snapshot is what the journal stores, so recovery never depends on a later profile edit.
        string source = WriteSource("doc.txt");
        Profile profile = Profile() with
        {
            Policies = new PolicySettings
            {
                ConflictResolution = ConflictResolution.RenameSuffix,
                OverwriteHandling = OverwriteHandling.DirectOverwrite,
                VerificationMethod = VerificationMethod.XxHash128,
                OnSuccess = OnSuccessAction.MoveToArchive,
                ArchiveFolder = @"C:\archive",
                OnFailure = OnFailureAction.AbortRestoreAndClean,
                MetadataOnConflict = MetadataOnConflict.FailJob,
            },
        };

        JobPlan plan = Build(profile, PayloadFor(source));

        Assert.Equal(ConflictResolution.RenameSuffix, plan.Policies.ConflictResolution);
        Assert.Equal(OverwriteHandling.DirectOverwrite, plan.Policies.OverwriteHandling);
        Assert.Equal(VerificationMethod.XxHash128, plan.Policies.Verification);
        Assert.Equal(OnSuccessAction.MoveToArchive, plan.Policies.OnSuccess);
        Assert.Equal(@"C:\archive", plan.Policies.ArchiveFolder);
        Assert.Equal(MetadataOnConflict.FailJob, plan.Policies.MetadataOnConflict);
    }

    [Fact]
    public void A_supplied_metadata_snapshot_is_used_without_re_reading_the_file()
    {
        // The scanner captures metadata for free during enumeration; reusing it avoids a second stat
        // and — importantly — means the plan describes what the scanner saw.
        string source = WriteSource("doc.txt", "payload");
        Core.Files.FileMetadata scanned = new()
        {
            Length = 4242,
            LastWritten = new DateTimeOffset(2020, 1, 2, 3, 4, 5, TimeSpan.Zero),
            Created = new DateTimeOffset(2019, 1, 1, 0, 0, 0, TimeSpan.Zero),
            IsHidden = false,
            IsSystem = false,
            IsSymlink = false,
        };

        JobPlan plan = Build(Profile(), PayloadFor(source) with { Metadata = scanned });

        Assert.Equal(4242, plan.Source.SizeBytes);
        Assert.Equal(scanned.LastWritten, plan.Source.LastWriteUtc);
    }

    [Fact]
    public void A_missing_source_fails_rather_than_planning_a_job_for_a_file_that_is_gone()
    {
        string missing = Path.Combine(_sourceRoot, "never-existed.txt");

        Result<JobPlan, JobError> result = _factory.Build(Profile(), PayloadFor(missing));

        Assert.True(result.TryGetError(out JobError? error));
        Assert.True(error!.Code is JobErrorCode.SourceDisposed or JobErrorCode.SourceUnreadable);
    }

    [Fact]
    public void Each_plan_gets_a_fresh_job_id_and_its_own_deterministic_workspace()
    {
        string source = WriteSource("doc.txt");

        JobPlan first = Build(Profile(), PayloadFor(source));
        JobPlan second = Build(Profile(), PayloadFor(source));

        Assert.NotEqual(first.JobId.Value, second.JobId.Value);
        Assert.NotEqual(first.WorkspaceDir, second.WorkspaceDir);
        // §7.2 row 2: the workspace is derivable from the job id alone, so recovery can find it.
        Assert.EndsWith(first.JobId.Value.ToString("N"), first.WorkspaceDir, StringComparison.Ordinal);
        Assert.Contains(".pipeline_tmp", first.WorkspaceDir, StringComparison.Ordinal);
        // A no-transformer job must not create it up front.
        Assert.False(Directory.Exists(first.WorkspaceDir));
    }

    [Fact]
    public void A_configured_temp_root_overrides_the_engine_work_directory()
    {
        string source = WriteSource("doc.txt");
        string custom = Path.Combine(_root, "custom-temp");
        JobPlanFactory factory = new(
            new EnginePaths { Root = Path.Combine(_root, "engine") },
            new EngineConfig { TempRoot = custom });

        Result<JobPlan, JobError> result = factory.Build(Profile(), PayloadFor(source));

        Assert.True(result.TryGetValue(out JobPlan? plan));
        Assert.StartsWith(custom, plan!.WorkspaceDir, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_plan_carries_the_profile_and_payload_through_unchanged()
    {
        // The executor reads filters and layout off plan.Profile, and the source root off plan.Payload.
        string source = WriteSource("doc.txt");
        Profile profile = Profile();
        Payload payload = PayloadFor(source);

        JobPlan plan = Build(profile, payload);

        Assert.Same(profile, plan.Profile);
        Assert.Same(payload, plan.Payload);
        Assert.Equal(profile.Id, plan.ProfileId);
    }
}
