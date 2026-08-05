using FileManager.Contracts.DryRun;
using FileManager.Contracts.IPC;
using FileManager.Contracts.Profiles;
using FileManager.Contracts.Settings;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FileManager.Contracts.Tests.IPC;

/// <summary>Pins the wire format: every polymorphic message round-trips through the
/// source-generated context with its "type" discriminator, and enums serialize as strings
/// (including the SHA256 JsonStringEnumMemberName override).</summary>
public sealed class SerializationTests
{
    private static readonly Guid SomeId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    public static TheoryData<IpcRequest, string> Requests() => new()
    {
        { new GetStatusRequest(), "get-status" },
        { new ListProfilesRequest(), "list-profiles" },
        { new GetProfileRequest { ProfileId = SomeId }, "get-profile" },
        { new SaveProfileRequest { Profile = SampleProfile(), AcknowledgeWarnings = true }, "save-profile" },
        { new DeleteProfileRequest { ProfileId = SomeId }, "delete-profile" },
        { new ValidateProfileRequest { Profile = SampleProfile() }, "validate-profile" },
        { new GetMatchingProfilesRequest { Path = @"C:\x" }, "get-matching" },
        { new RunProfileRequest { ProfileId = SomeId, Path = @"C:\x" }, "run-profile" },
        { new SetPausedRequest { Paused = true }, "set-paused" },
        { new DryRunRequest { ProfileId = SomeId, ScopePath = @"C:\x" }, "dry-run" },
        { new DryRunStreamRequest { ProfileId = SomeId, ScopePath = @"C:\x" }, "dry-run-stream" },
        { new GetRecentJobsRequest(), "get-recent-jobs" },
        { new GetJobLogRequest { JobId = SomeId }, "get-job-log" },
        { new SubscribeEventsRequest(), "subscribe" },
        { new GetSettingsRequest(), "get-settings" },
        { new UpdateSettingsRequest { Settings = GlobalSettings.Default }, "update-settings" },
        { new ShutdownRequest(), "shutdown" },
        { new ApproveRunRequest { RunId = SomeId, Approve = true }, "approve-run" },
        { new CancelRunRequest { RunId = SomeId }, "cancel-run" },
        { new GetRunPlanStreamRequest { RunId = SomeId }, "get-run-plan-stream" },
    };

    [Theory]
    [MemberData(nameof(Requests))]
    public void Requests_round_trip_with_discriminator(IpcRequest request, string discriminator)
    {
        byte[] wire = IpcSerializer.SerializeRequest(request);

        using JsonDocument document = JsonDocument.Parse(wire);
        Assert.Equal(discriminator, document.RootElement.GetProperty("type").GetString());

        Assert.True(IpcSerializer.DeserializeRequest(wire).TryGetValue(out IpcRequest? roundTripped));
        Assert.Equal(request.GetType(), roundTripped.GetType());
    }

    public static TheoryData<IpcResponse, string> Responses() => new()
    {
        { new OkResponse(), "ok" },
        { new ErrorResponse { Code = "X", Message = "y" }, "error" },
        { new StatusResponse { Status = new EngineStatusSnapshot(false, 1, 0, 0, null) }, "status" },
        { new ProfileListResponse { Profiles = [new ProfileSummary(SomeId, "p", true, "Manual")] }, "profile-list" },
        { new ProfileResponse { Profile = SampleProfile() }, "profile" },
        { new ValidationResponse { Issues = [new ValidationIssue(ValidationSeverity.BlockingWarning, "C", "m")] }, "validation" },
        { new MatchingProfilesResponse { Matches = [] }, "matching" },
        { new RunProfileResponse { RunId = SomeId }, "run-profile-result" },
        { new DryRunResponse { Report = SampleReport() }, "dry-run-report" },
        { DryRunColumns.ToChunk(SampleDirectories(), sourceFiles: [SampleWireFile()], sourceOperations: [SampleWireSourceOp()]), "dry-run-chunk" },
        { new DryRunProgressResponse { Phase = DryRunProgressPhase.ScanningSources, SourceFiles = 1, DestinationFiles = 2 }, "dry-run-progress" },
        { new DryRunCompleteResponse { GeneratedAt = DateTimeOffset.UnixEpoch, Truncated = false }, "dry-run-complete" },
        { new RecentJobsResponse { Jobs = [] }, "recent-jobs" },
        { new JobLogResponse { Lines = ["a"] }, "job-log" },
        { new SettingsResponse { Settings = GlobalSettings.Default }, "settings" },
    };

    [Theory]
    [MemberData(nameof(Responses))]
    public void Responses_round_trip_with_discriminator(IpcResponse response, string discriminator)
    {
        byte[] wire = IpcSerializer.SerializeResponse(response);

        using JsonDocument document = JsonDocument.Parse(wire);
        Assert.Equal(discriminator, document.RootElement.GetProperty("type").GetString());

        Assert.True(IpcSerializer.DeserializeResponse(wire).TryGetValue(out IpcResponse? roundTripped));
        Assert.Equal(response.GetType(), roundTripped.GetType());
    }

    public static TheoryData<EngineEvent, string> Events() => new()
    {
        { new JobStartedEvent { AtUtc = DateTimeOffset.UnixEpoch, JobId = SomeId, ProfileId = SomeId, SourcePath = @"C:\x" }, "job-started" },
        { new JobProgressEvent { AtUtc = DateTimeOffset.UnixEpoch, JobId = SomeId, Phase = JobPhase.Distributing, TargetsCompleted = 1, TargetCount = 2 }, "job-progress" },
        { new RunQueuedEvent { AtUtc = DateTimeOffset.UnixEpoch, ProfileId = SomeId, ScopePath = "C:/in", QueuedCount = 3, RunId = SomeId }, "run-queued" },
        { new RunPlannedEvent { AtUtc = DateTimeOffset.UnixEpoch, RunId = SomeId, ProfileId = SomeId, PlannedCopies = 7, PlannedDeletes = 2, PlannedCopyBytes = 900, PlannedDeleteBytes = 80, Truncated = false }, "run-planned" },
        { new RunProgressEvent { AtUtc = DateTimeOffset.UnixEpoch, RunId = SomeId, Phase = "Executing", Completed = 3, Total = 7, Deleted = 1 }, "run-progress" },
        { new RunCompletedEvent { AtUtc = DateTimeOffset.UnixEpoch, RunId = SomeId, ProfileId = SomeId, Outcome = "Succeeded", Succeeded = 7, Skipped = 0, Failed = 0, Deleted = 2, BytesDeleted = 80 }, "run-completed" },
        { new PauseChangedEvent { AtUtc = DateTimeOffset.UnixEpoch, Paused = true }, "pause-changed" },
        { new ProfilesChangedEvent { AtUtc = DateTimeOffset.UnixEpoch }, "profiles-changed" },
        { new EngineWarningEvent { AtUtc = DateTimeOffset.UnixEpoch, Message = "w" }, "engine-warning" },
    };

    [Theory]
    [MemberData(nameof(Events))]
    public void Events_round_trip_with_discriminator(EngineEvent evt, string discriminator)
    {
        byte[] wire = IpcSerializer.SerializeEvent(evt);

        using JsonDocument document = JsonDocument.Parse(wire);
        Assert.Equal(discriminator, document.RootElement.GetProperty("type").GetString());

        Assert.True(IpcSerializer.DeserializeEvent(wire).TryGetValue(out EngineEvent? roundTripped));
        Assert.Equal(evt.GetType(), roundTripped.GetType());
    }

    /// <summary>A deliberate tripwire: the protocol version gates UI/service compatibility
    /// (IPC_VERSION_MISMATCH), so it must never move as an incidental side effect of an edit.</summary>
    [Fact]
    public void Current_protocol_version_is_pinned()
    {
        Assert.Equal(11, IpcRequest.CurrentProtocolVersion);
    }

    /// <summary>JobPhase must stay a string on the wire (the context sets UseStringEnumConverter), so
    /// a consumer reading it never depends on the enum's numeric ordering.</summary>
    [Fact]
    public void Job_phase_serializes_as_a_string()
    {
        byte[] wire = IpcSerializer.SerializeEvent(new JobProgressEvent
        {
            AtUtc = DateTimeOffset.UnixEpoch,
            JobId = SomeId,
            Phase = JobPhase.Distributing,
            TargetsCompleted = 1,
            TargetCount = 2,
        });

        using JsonDocument document = JsonDocument.Parse(wire);
        Assert.Equal("Distributing", document.RootElement.GetProperty("Phase").GetString());
    }

    [Fact]
    public void Enums_serialize_as_strings_and_sha256_uses_its_wire_name()
    {
        string json = Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(
            SampleProfile(), FileManagerJsonContext.Default.Profile));

        Assert.Contains("\"SHA256\"", json);                 // [JsonStringEnumMemberName("SHA256")]
        Assert.Contains("\"AdditiveArchive\"", json);        // string, not int
        Assert.Contains("\"StageOverwrites\"", json);
        Assert.Contains("\"FailuresAndSkips\"", json);

        Profile? roundTripped = JsonSerializer.Deserialize(json, FileManagerJsonContext.Default.Profile);
        Assert.NotNull(roundTripped);
        Assert.Equal(VerificationMethod.Sha256, roundTripped.Policies.VerificationMethod);
    }

    [Fact]
    public void XxHash128_uses_its_wire_name_and_round_trips()
    {
        Profile sample = SampleProfile();
        sample = sample with { Policies = sample.Policies with { VerificationMethod = VerificationMethod.XxHash128 } };

        string json = Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(
            sample, FileManagerJsonContext.Default.Profile));

        Assert.Contains("\"XXH3-128\"", json);               // [JsonStringEnumMemberName("XXH3-128")]

        Profile? roundTripped = JsonSerializer.Deserialize(json, FileManagerJsonContext.Default.Profile);
        Assert.NotNull(roundTripped);
        Assert.Equal(VerificationMethod.XxHash128, roundTripped.Policies.VerificationMethod);
    }

    [Fact]
    public void ScanDestination_round_trips_and_defaults_to_false_when_absent()
    {
        Profile sample = SampleProfile() with { ScanDestination = true };
        string json = Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(
            sample, FileManagerJsonContext.Default.Profile));

        Profile? roundTripped = JsonSerializer.Deserialize(json, FileManagerJsonContext.Default.Profile);
        Assert.NotNull(roundTripped);
        Assert.True(roundTripped.ScanDestination);

        // Legacy JSON that predates the field must deserialize to false (additive, backward compatible).
        string legacy = json.Replace(",\"ScanDestination\":true", "");
        Assert.DoesNotContain("ScanDestination", legacy);
        Profile? fromLegacy = JsonSerializer.Deserialize(legacy, FileManagerJsonContext.Default.Profile);
        Assert.NotNull(fromLegacy);
        Assert.False(fromLegacy.ScanDestination);
    }

    [Theory]
    [InlineData(SyncMode.AdditiveArchive, false, false)]   // Additive honors the flag
    [InlineData(SyncMode.AdditiveArchive, true, true)]
    [InlineData(SyncMode.Mirror, false, true)]             // Mirror always sweeps, flag or not
    [InlineData(SyncMode.Mirror, true, true)]
    public void EffectiveScanDestination_forces_the_sweep_on_in_mirror(
        SyncMode mode, bool scanDestination, bool expected)
    {
        Profile profile = SampleProfile() with { SyncMode = mode, ScanDestination = scanDestination };
        Assert.Equal(expected, profile.EffectiveScanDestination);
        Assert.Equal(expected, Profile.ComputeEffectiveScanDestination(mode, scanDestination));
    }

    [Fact]
    public void EffectiveScanDestination_is_not_serialized()
    {
        // [JsonIgnore] — it is a derived convenience over SyncMode + ScanDestination, never a wire field.
        Profile sample = SampleProfile() with { SyncMode = SyncMode.Mirror, ScanDestination = false };
        string json = Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(
            sample, FileManagerJsonContext.Default.Profile));
        Assert.DoesNotContain("EffectiveScanDestination", json);
    }

    [Fact]
    public void DryRunReport_round_trips_all_four_collections_with_indices()
    {
        byte[] wire = IpcSerializer.SerializeResponse(new DryRunResponse { Report = SampleReport() });

        // OperationKind serializes as a string, consistent with the rest of the wire format.
        using JsonDocument document = JsonDocument.Parse(wire);
        Assert.Equal("Processed", document.RootElement
            .GetProperty("Report").GetProperty("SourceOperations")[0].GetProperty("Kind").GetString());
        // Files carry FileName + a directory index on the wire, not a flat "Path" string.
        Assert.Equal("one.txt", document.RootElement
            .GetProperty("Report").GetProperty("SourceFiles")[0].GetProperty("FileName").GetString());
        Assert.True(document.RootElement.GetProperty("Report").GetProperty("Directories").GetArrayLength() > 0);

        Assert.True(IpcSerializer.DeserializeResponse(wire).TryGetValue(out IpcResponse? reparsed));
        DryRunReport report = Assert.IsType<DryRunResponse>(reparsed).Report;

        // Files carry (DirIndex, FileName) into the shared table; resolve them back to paths.
        string[] paths = DryRunDirectoryTable.Materialize(report.Directories);
        DryRunFile sourceFile = Assert.Single(report.SourceFiles);
        Assert.Equal(@"C:\a\one.txt", System.IO.Path.Join(paths[sourceFile.DirIndex], sourceFile.FileName));
        DryRunFile destinationFile = Assert.Single(report.DestinationFiles);
        Assert.Equal(@"C:\t\one.txt", System.IO.Path.Join(paths[destinationFile.DirIndex], destinationFile.FileName));

        DryRunOperation sourceOp = Assert.Single(report.SourceOperations);
        Assert.Equal(OperationKind.Processed, sourceOp.Kind);
        Assert.Equal(0, sourceOp.SourceIndex);
        Assert.Equal(OnSuccessAction.KeepSource, sourceOp.SourceDisposition);

        // Indices survive the round-trip and resolve into the file lists.
        DryRunOperation destOp = Assert.Single(report.DestinationOperations);
        Assert.Equal(OperationKind.Overwrite, destOp.Kind);
        Assert.Equal(0, destOp.SourceIndex);
        Assert.Equal(0, destOp.SubjectIndex);
        DryRunFile origin = report.SourceFiles[destOp.SourceIndex];
        Assert.Equal(@"C:\a\one.txt", System.IO.Path.Join(paths[origin.DirIndex], origin.FileName));
        DryRunFile subject = report.DestinationFiles[destOp.SubjectIndex];
        Assert.Equal(@"C:\t\one.txt", System.IO.Path.Join(paths[subject.DirIndex], subject.FileName));
    }

    [Fact]
    public void DryRunReport_truncated_flag_round_trips_and_defaults_false_when_absent()
    {
        byte[] wire = IpcSerializer.SerializeResponse(new DryRunResponse { Report = SampleReport() with { Truncated = true } });
        using JsonDocument document = JsonDocument.Parse(wire);
        Assert.True(document.RootElement.GetProperty("Report").GetProperty("Truncated").GetBoolean());

        Assert.True(IpcSerializer.DeserializeResponse(wire).TryGetValue(out IpcResponse? reparsed));
        Assert.True(Assert.IsType<DryRunResponse>(reparsed).Report.Truncated);

        // A report whose "Truncated" property is absent deserializes to the default, false.
        string json = Encoding.UTF8.GetString(IpcSerializer.SerializeResponse(new DryRunResponse { Report = SampleReport() }));
        JsonObject obj = JsonNode.Parse(json)!.AsObject();
        Assert.True(obj["Report"]!.AsObject().Remove("Truncated"));
        Assert.True(IpcSerializer.DeserializeResponse(Encoding.UTF8.GetBytes(obj.ToJsonString())).TryGetValue(out IpcResponse? without));
        Assert.False(Assert.IsType<DryRunResponse>(without).Report.Truncated);
    }

    [Fact]
    public void DryRunFile_round_trips_standalone()
    {
        DryRunFile file = new()
        {
            DirIndex = 1,
            FileName = "one.txt",
            RootDirIndex = 1,
            Length = 42,
            LastWritten = DateTimeOffset.UnixEpoch,
            IsReparsePoint = true,
        };
        byte[] wire = JsonSerializer.SerializeToUtf8Bytes(file, FileManagerJsonContext.Default.DryRunFile);
        DryRunFile? roundTripped = JsonSerializer.Deserialize(wire, FileManagerJsonContext.Default.DryRunFile);
        Assert.Equal(file, roundTripped);
    }

    [Fact]
    public void DryRunOperation_round_trips_standalone()
    {
        DryRunOperation op = new()
        {
            DirIndex = 2,
            FileName = "one (1).txt",
            RootDirIndex = 2,
            Kind = OperationKind.Rename,
            SourceIndex = 3,
            SubjectIndex = -1,
            SourceDisposition = OnSuccessAction.MoveToTrash,
            Detail = "renamed to avoid a conflict",
        };
        byte[] wire = JsonSerializer.SerializeToUtf8Bytes(op, FileManagerJsonContext.Default.DryRunOperation);
        DryRunOperation? roundTripped = JsonSerializer.Deserialize(wire, FileManagerJsonContext.Default.DryRunOperation);
        Assert.Equal(op, roundTripped);
    }

    [Fact]
    public void DryRunDirectory_round_trips_standalone()
    {
        DryRunDirectory root = new(@"C:\", -1);
        DryRunDirectory child = new("a", 0);
        Assert.Equal(root, JsonSerializer.Deserialize(
            JsonSerializer.SerializeToUtf8Bytes(root, FileManagerJsonContext.Default.DryRunDirectory),
            FileManagerJsonContext.Default.DryRunDirectory));
        Assert.Equal(child, JsonSerializer.Deserialize(
            JsonSerializer.SerializeToUtf8Bytes(child, FileManagerJsonContext.Default.DryRunDirectory),
            FileManagerJsonContext.Default.DryRunDirectory));
    }

    [Fact]
    public void DryRunChunkResponse_round_trips_its_directory_table_and_collections()
    {
        // Normalized wire shape: a directory table plus (DirIndex, FileName) records, built the
        // same way the service builds them.
        DryRunDirectoryTableBuilder dirs = new();
        DryRunFile sourceFile = dirs.Convert(SampleSourceFile());
        DryRunFile destinationFile = dirs.Convert(SampleDestinationFile());
        DryRunOperation sourceOp = dirs.Convert(SampleSourceOp());
        DryRunOperation destOp = dirs.Convert(new VirtualFileOperation
        {
            Path = @"C:\t\one.txt", Root = @"C:\t", Kind = OperationKind.Overwrite, SourceIndex = 0, SubjectIndex = 0,
        });
        byte[] wire = IpcSerializer.SerializeResponse(DryRunColumns.ToChunk(
            dirs.FlushNew(), [sourceFile], [destinationFile], [sourceOp], [destOp]));

        Assert.True(IpcSerializer.DeserializeResponse(wire).TryGetValue(out IpcResponse? reparsed));
        DryRunChunkResponse chunk = Assert.IsType<DryRunChunkResponse>(reparsed);

        // The table survives the round-trip and resolves the records back to the original paths.
        string[] paths = DryRunDirectoryTable.Materialize(
            [.. chunk.DirectoryName.Select((name, i) => new DryRunDirectory(name, chunk.DirectoryParentIndex[i]))]);
        Assert.Equal(1, chunk.SourceFiles.Count);
        Assert.Equal(@"C:\a\one.txt", System.IO.Path.Join(paths[chunk.SourceFiles.DirIndex[0]], chunk.SourceFiles.FileName[0]));
        Assert.Equal(@"C:\a", paths[chunk.SourceFiles.RootDirIndex[0]]);
        Assert.Equal(1, chunk.DestinationFiles.Count);
        Assert.Equal(@"C:\t\one.txt", System.IO.Path.Join(paths[chunk.DestinationFiles.DirIndex[0]], chunk.DestinationFiles.FileName[0]));
        Assert.Equal(OperationKind.Processed, Assert.Single(chunk.SourceOperations.Kind));
        Assert.Equal(1, chunk.DestinationOperations.Count);
        Assert.Equal(0, chunk.DestinationOperations.SourceIndex[0]);
        Assert.Equal(0, chunk.DestinationOperations.SubjectIndex[0]);
    }

    [Fact]
    public void SpaceProjection_round_trips_on_the_completion_frame()
    {
        var projection = new SpaceProjection
        {
            TotalBytesWritten = 5_000_000,
            TotalNetChangeBytes = -1_234,
            SafetyMarginBytes = 64L * 1024 * 1024,
            Volumes =
            [
                new VolumeSpaceEstimate
                {
                    VolumeRoot = "D:",
                    CapacityKnown = true,
                    TotalCapacityBytes = 2_000_000_000,
                    UsedNowBytes = 500_000_000,
                    FreeNowBytes = 1_500_000_000,
                    ClusterBytes = 4096,
                    BytesWrittenBytes = 5_000_000,
                    NetChangeBytes = -1_234,
                    SettledUsedBytes = 499_998_766,
                    DeferredReclaimBytes = 12_000_000,
                    MirrorDeferredReclaimBytes = 9_000_000,
                    RealisticPeakUsedBytes = 520_000_000,
                    SafeCeilingUsedBytes = 560_000_000,
                    Folders = [new FolderSpaceBreakdown { Root = @"D:\backup", BytesWrittenBytes = 5_000_000, NetChangeBytes = -1_234, FileCount = 3 }],
                },
            ],
        };

        byte[] wire = IpcSerializer.SerializeResponse(new DryRunCompleteResponse
        {
            GeneratedAt = DateTimeOffset.UnixEpoch,
            Truncated = false,
            Space = projection,
        });

        Assert.True(IpcSerializer.DeserializeResponse(wire).TryGetValue(out IpcResponse? reparsed));
        SpaceProjection? space = Assert.IsType<DryRunCompleteResponse>(reparsed).Space;
        Assert.NotNull(space);
        Assert.Equal(5_000_000, space.TotalBytesWritten);
        Assert.Equal(-1_234, space.TotalNetChangeBytes);
        VolumeSpaceEstimate volume = Assert.Single(space.Volumes);
        Assert.Equal("D:", volume.VolumeRoot);
        Assert.True(volume.CapacityKnown);
        Assert.Equal(4096, volume.ClusterBytes);
        Assert.Equal(12_000_000, volume.DeferredReclaimBytes);
        Assert.Equal(9_000_000, volume.MirrorDeferredReclaimBytes);
        Assert.Equal(560_000_000, volume.SafeCeilingUsedBytes);
        FolderSpaceBreakdown folder = Assert.Single(volume.Folders);
        Assert.Equal(@"D:\backup", folder.Root);
        Assert.Equal(3, folder.FileCount);
    }

    [Fact]
    public void DryRunCompleteResponse_space_defaults_null_when_absent()
    {
        byte[] wire = IpcSerializer.SerializeResponse(
            new DryRunCompleteResponse { GeneratedAt = DateTimeOffset.UnixEpoch, Truncated = false });
        Assert.True(IpcSerializer.DeserializeResponse(wire).TryGetValue(out IpcResponse? reparsed));
        Assert.Null(Assert.IsType<DryRunCompleteResponse>(reparsed).Space);
    }

    [Fact]
    public void GlobalSettings_round_trips_its_fields_over_the_wire()
    {
        byte[] wire = IpcSerializer.SerializeResponse(new SettingsResponse
        {
            Settings = new GlobalSettings
            {
                ServiceStartupMode = ServiceStartupMode.RunOnStartup,
                ScanThreading = new ScanThreadingSettings
                {
                    MaxScanThreads = ThreadBudget.Explicit(6),
                    MaxHashThreads = ThreadBudget.Auto,
                    PerDriveDefault = ThreadBudget.Explicit(4),
                    DriveTypeOverrides = new Dictionary<DriveClass, ThreadBudget> { [DriveClass.Network] = ThreadBudget.Explicit(16) },
                    SpecificDriveOverrides = new Dictionary<string, ThreadBudget> { ["c:"] = ThreadBudget.Explicit(2) },
                },
            },
        });
        Assert.True(IpcSerializer.DeserializeResponse(wire).TryGetValue(out IpcResponse? reparsed));
        SettingsResponse roundTripped = Assert.IsType<SettingsResponse>(reparsed);
        Assert.Equal(ServiceStartupMode.RunOnStartup, roundTripped.Settings.ServiceStartupMode);

        ScanThreadingSettings st = roundTripped.Settings.ScanThreading;
        Assert.Equal(6, st.MaxScanThreads.Value);
        Assert.True(st.MaxHashThreads.IsAuto);
        Assert.Equal(4, st.PerDriveDefault.Value);
        Assert.Equal(16, st.DriveTypeOverrides[DriveClass.Network].Value);
        Assert.Equal(2, st.SpecificDriveOverrides["c:"].Value);

        // Enums serialize as strings, consistent with the rest of the wire format.
        using JsonDocument document = JsonDocument.Parse(wire);
        Assert.Equal("RunOnStartup",
            document.RootElement.GetProperty("Settings").GetProperty("ServiceStartupMode").GetString());
        // The theme is deliberately absent: it is client-side state (client-settings.json) and never
        // crosses the wire, so the engine's settings frame must not carry it.
        Assert.False(document.RootElement.GetProperty("Settings").TryGetProperty("ThemeMode", out _));
    }

    [Fact]
    public void GlobalSettings_with_stale_concurrency_fields_still_deserializes()
    {
        // A settings.json written before v2 carries the removed DryRunConcurrencyMode/DryRunManualWorkers,
        // and one written before v5 also carries ThemeMode (which moved to the UI's client-settings.json).
        // The source-gen deserializer skips unknown members, so no migration pass is needed here — the
        // theme's own migration is the UI's, in ClientSettingsStore.
        JsonObject obj = new()
        {
            ["SchemaVersion"] = 1,
            ["ServiceStartupMode"] = "RunOnStartup",
            ["DryRunConcurrencyMode"] = "Manual",
            ["DryRunManualWorkers"] = 3,
            ["ThemeMode"] = "Dark",
        };

        GlobalSettings? parsed = JsonSerializer.Deserialize(obj.ToJsonString(), FileManagerJsonContext.Default.GlobalSettings);
        Assert.NotNull(parsed);
        Assert.Equal(ServiceStartupMode.RunOnStartup, parsed!.ServiceStartupMode);
        Assert.True(parsed.ScanThreading.MaxScanThreads.IsAuto);   // the new field defaults to auto
    }

    [Fact]
    public void Profile_with_a_stale_concurrency_field_still_deserializes()
    {
        // A profile written before the Concurrency field was removed carries an unknown "Concurrency"
        // object; the deserializer ignores it (no migration needed).
        string json = Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(
            SampleProfile(), FileManagerJsonContext.Default.Profile));
        JsonObject obj = JsonNode.Parse(json)!.AsObject();
        obj["Concurrency"] = new JsonObject { ["Mode"] = "Manual", ["ManualWorkers"] = 4 };

        Profile? parsed = JsonSerializer.Deserialize(obj.ToJsonString(), FileManagerJsonContext.Default.Profile);
        Assert.NotNull(parsed);
        Assert.Equal(SampleProfile().Name, parsed!.Name);   // the rest parsed fine, stale field dropped
    }

    [Fact]
    public void Profile_saved_before_MirrorDeletion_existed_loads_as_delete_after_copy()
    {
        // The additive-field contract: a profile written before the policy was introduced has no
        // MirrorDeletion member, and must land on the safe timing rather than on whatever the
        // source generator leaves behind (it does not run property initializers for absent members).
        string json = Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(
            SampleProfile(), FileManagerJsonContext.Default.Profile));
        JsonObject obj = JsonNode.Parse(json)!.AsObject();
        obj["Policies"]!.AsObject().Remove("MirrorDeletion");

        Profile? parsed = JsonSerializer.Deserialize(obj.ToJsonString(), FileManagerJsonContext.Default.Profile);
        Assert.NotNull(parsed);
        Assert.Equal(MirrorDeletion.AfterCopy, parsed!.Policies.MirrorDeletion);
    }

    [Fact]
    public void Profile_saved_before_LargeFileIdentity_existed_loads_as_the_exact_full_hash()
    {
        // The additive-field contract for the §3.4.1 identity policy. Both halves matter:
        //  - the METHOD must land on FullHash (slot 0), the pre-existing exact behaviour;
        //  - the THRESHOLD must land on 256 MiB, NOT on 0. A plain long would read back as 0, which means
        //    "every file is large" — i.e. an upgrading user would be silently switched to the cheap check.
        string json = Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(
            SampleProfile(), FileManagerJsonContext.Default.Profile));
        JsonObject obj = JsonNode.Parse(json)!.AsObject();
        obj["Policies"]!.AsObject().Remove("LargeFileIdentity");
        obj["Policies"]!.AsObject().Remove("LargeFileIdentityThresholdBytes");

        Profile? parsed = JsonSerializer.Deserialize(obj.ToJsonString(), FileManagerJsonContext.Default.Profile);
        Assert.NotNull(parsed);
        Assert.Equal(LargeFileIdentity.FullHash, parsed!.Policies.LargeFileIdentity);
        Assert.Equal(PolicySettings.DefaultLargeFileIdentityThresholdBytes,
            parsed.Policies.LargeFileIdentityThresholdBytes);
    }

    [Fact]
    public void A_non_default_identity_threshold_round_trips()
    {
        Profile original = SampleProfile();
        original = original with
        {
            Policies = original.Policies with
            {
                LargeFileIdentity = LargeFileIdentity.SampledHash,
                LargeFileIdentityThresholdBytes = 4096,
            },
        };

        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(original, FileManagerJsonContext.Default.Profile);
        Profile? parsed = JsonSerializer.Deserialize(bytes, FileManagerJsonContext.Default.Profile);

        Assert.NotNull(parsed);
        Assert.Equal(LargeFileIdentity.SampledHash, parsed!.Policies.LargeFileIdentity);
        Assert.Equal(4096, parsed.Policies.LargeFileIdentityThresholdBytes);
    }

    [Fact]
    public void The_default_identity_threshold_stays_absent_from_the_json()
    {
        // The nullable-backing pattern keeps the default off the wire, so an omitted field and an explicit
        // 256 MiB compare equal under the record's value-equality.
        Profile original = SampleProfile();
        Assert.Equal(PolicySettings.DefaultLargeFileIdentityThresholdBytes,
            original.Policies.LargeFileIdentityThresholdBytes);

        string json = Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(
            original, FileManagerJsonContext.Default.Profile));
        Assert.DoesNotContain("LargeFileIdentityThresholdBytes", json, StringComparison.Ordinal);

        Profile explicitly = original with
        {
            Policies = original.Policies with
            {
                LargeFileIdentityThresholdBytes = PolicySettings.DefaultLargeFileIdentityThresholdBytes,
            },
        };
        Assert.Equal(original, explicitly);
    }

    [Fact]
    public void Malformed_and_unknown_discriminator_payloads_deserialize_to_failures()
    {
        Assert.True(IpcSerializer.DeserializeRequest("not json"u8.ToArray()).TryGetError(out string? malformed));
        Assert.Contains("malformed request", malformed);
        Assert.True(IpcSerializer.DeserializeRequest("""{"type":"no-such-request"}"""u8.ToArray()).TryGetError(out string? unknown));
        Assert.Contains("malformed request", unknown);
    }

    /// <summary>The table for <see cref="SampleWireFile"/>/<see cref="SampleWireSourceOp"/>:
    /// <c>C:\</c> → <c>a</c>, so index 1 is <c>C:\a</c>.</summary>
    private static List<DryRunDirectory> SampleDirectories() =>
        [new DryRunDirectory(@"C:\", -1), new DryRunDirectory("a", 0)];

    private static DryRunFile SampleWireFile() => new()
    {
        DirIndex = 1,
        FileName = "one.txt",
        RootDirIndex = 1,
        Length = 10,
        LastWritten = DateTimeOffset.UnixEpoch,
    };

    private static DryRunOperation SampleWireSourceOp() => new()
    {
        DirIndex = 1,
        FileName = "one.txt",
        RootDirIndex = 1,
        Kind = OperationKind.Processed,
        SourceIndex = 0,
        SourceDisposition = OnSuccessAction.KeepSource,
    };

    private static PhysicalFile SampleSourceFile() => new()
    {
        Path = @"C:\a\one.txt",
        Root = @"C:\a",
        Length = 10,
        LastWritten = DateTimeOffset.UnixEpoch,
    };

    private static PhysicalFile SampleDestinationFile() => new()
    {
        Path = @"C:\t\one.txt",
        Root = @"C:\t",
        Length = 20,
        LastWritten = DateTimeOffset.UnixEpoch,
    };

    private static VirtualFileOperation SampleSourceOp() => new()
    {
        Path = @"C:\a\one.txt",
        Root = @"C:\a",
        Kind = OperationKind.Processed,
        SourceIndex = 0,
        SourceDisposition = OnSuccessAction.KeepSource,
    };

    /// <summary>A minimal report exercising all four collections: one source file
    /// (<c>C:\a\one.txt</c>) overwritten at one destination (<c>C:\t\one.txt</c>), with the
    /// destination op referencing both by index. Built through the directory-table builder,
    /// the same way the service builds reports.</summary>
    private static DryRunReport SampleReport()
    {
        DryRunDirectoryTableBuilder dirs = new();
        DryRunFile sourceFile = dirs.Convert(SampleSourceFile());
        DryRunFile destinationFile = dirs.Convert(SampleDestinationFile());
        DryRunOperation sourceOp = dirs.Convert(SampleSourceOp());
        DryRunOperation destOp = dirs.Convert(new VirtualFileOperation
        {
            Path = @"C:\t\one.txt",
            Root = @"C:\t",
            Kind = OperationKind.Overwrite,
            SourceIndex = 0,
            SubjectIndex = 0,
            Detail = "existing file last modified 1970-01-01",
        });
        return new DryRunReport
        {
            ProfileId = SomeId,
            GeneratedAt = DateTimeOffset.UnixEpoch,
            Directories = dirs.Entries,
            SourceFiles = [sourceFile],
            DestinationFiles = [destinationFile],
            SourceOperations = [sourceOp],
            DestinationOperations = [destOp],
        };
    }

    internal static Profile SampleProfile() => new()
    {
        SchemaVersion = 2,
        Id = SomeId,
        Name = "Sample",
        Active = true,
        SyncMode = SyncMode.AdditiveArchive,
        TargetLayout = TargetLayout.PreserveStructure,
        Triggers = new TriggerSettings { ManualShell = true, Watcher = false, Schedule = null },
        Sources = [new SourceConfig { Path = @"C:\sample-src" }],
        Targets = [new TargetConfig { Path = @"C:\sample-dst" }],
        Policies = new PolicySettings
        {
            ConflictResolution = ConflictResolution.Skip,
            OverwriteHandling = OverwriteHandling.StageOverwrites,
            VerificationMethod = VerificationMethod.Sha256,
            OnSuccess = OnSuccessAction.KeepSource,
            ArchiveFolder = null,
            OnFailure = OnFailureAction.AbortRestoreAndClean,
            MetadataOnConflict = MetadataOnConflict.WarnAndContinue,
        },
        Filters = null,
        Logging = new LoggingSettings { Verbosity = LogVerbosity.FailuresAndSkips, NotifyOnFailure = true },
    };
}
