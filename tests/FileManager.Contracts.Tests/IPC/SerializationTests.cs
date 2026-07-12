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
        { new DryRunResponse { Report = SampleReport() }, "dry-run-report" },
        { new DryRunChunkResponse { SourceFiles = [SampleSourceFile()], SourceOperations = [SampleSourceOp()] }, "dry-run-chunk" },
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
    public void DryRunReport_round_trips_all_four_collections_with_indices()
    {
        byte[] wire = IpcSerializer.SerializeResponse(new DryRunResponse { Report = SampleReport() });

        // OperationKind serializes as a string, consistent with the rest of the wire format.
        using JsonDocument document = JsonDocument.Parse(wire);
        Assert.Equal("Processed", document.RootElement
            .GetProperty("Report").GetProperty("SourceOperations")[0].GetProperty("Kind").GetString());

        Assert.True(IpcSerializer.DeserializeResponse(wire).TryGetValue(out IpcResponse? reparsed));
        DryRunReport report = Assert.IsType<DryRunResponse>(reparsed).Report;

        Assert.Equal(@"C:\a\one.txt", Assert.Single(report.SourceFiles).Path);
        Assert.Equal(@"C:\t\one.txt", Assert.Single(report.DestinationFiles).Path);

        VirtualFileOperation sourceOp = Assert.Single(report.SourceOperations);
        Assert.Equal(OperationKind.Processed, sourceOp.Kind);
        Assert.Equal(0, sourceOp.SourceIndex);
        Assert.Equal(OnSuccessAction.KeepSource, sourceOp.SourceDisposition);

        // Indices survive the round-trip and resolve into the file lists.
        VirtualFileOperation destOp = Assert.Single(report.DestinationOperations);
        Assert.Equal(OperationKind.Overwrite, destOp.Kind);
        Assert.Equal(0, destOp.SourceIndex);
        Assert.Equal(0, destOp.SubjectIndex);
        Assert.Equal(@"C:\a\one.txt", report.SourceFiles[destOp.SourceIndex].Path);
        Assert.Equal(@"C:\t\one.txt", report.DestinationFiles[destOp.SubjectIndex].Path);
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
    public void PhysicalFile_round_trips_standalone()
    {
        PhysicalFile file = new()
        {
            Path = @"C:\a\one.txt",
            Root = @"C:\a",
            Length = 42,
            LastWritten = DateTimeOffset.UnixEpoch,
            IsReparsePoint = true,
        };
        byte[] wire = JsonSerializer.SerializeToUtf8Bytes(file, FileManagerJsonContext.Default.PhysicalFile);
        PhysicalFile? roundTripped = JsonSerializer.Deserialize(wire, FileManagerJsonContext.Default.PhysicalFile);
        Assert.Equal(file, roundTripped);
    }

    [Fact]
    public void VirtualFileOperation_round_trips_standalone()
    {
        VirtualFileOperation op = new()
        {
            Path = @"C:\t\one (1).txt",
            Root = @"C:\t",
            Kind = OperationKind.Rename,
            SourceIndex = 3,
            SubjectIndex = -1,
            SourceDisposition = OnSuccessAction.MoveToTrash,
            Detail = "renamed to avoid a conflict",
        };
        byte[] wire = JsonSerializer.SerializeToUtf8Bytes(op, FileManagerJsonContext.Default.VirtualFileOperation);
        VirtualFileOperation? roundTripped = JsonSerializer.Deserialize(wire, FileManagerJsonContext.Default.VirtualFileOperation);
        Assert.Equal(op, roundTripped);
    }

    [Fact]
    public void DryRunChunkResponse_round_trips_its_four_collections()
    {
        byte[] wire = IpcSerializer.SerializeResponse(new DryRunChunkResponse
        {
            SourceFiles = [SampleSourceFile()],
            DestinationFiles = [SampleDestinationFile()],
            SourceOperations = [SampleSourceOp()],
            DestinationOperations =
            [
                new VirtualFileOperation { Path = @"C:\t\one.txt", Root = @"C:\t", Kind = OperationKind.Overwrite, SourceIndex = 0, SubjectIndex = 0 },
            ],
        });

        Assert.True(IpcSerializer.DeserializeResponse(wire).TryGetValue(out IpcResponse? reparsed));
        DryRunChunkResponse chunk = Assert.IsType<DryRunChunkResponse>(reparsed);
        Assert.Equal(@"C:\a\one.txt", Assert.Single(chunk.SourceFiles).Path);
        Assert.Equal(@"C:\t\one.txt", Assert.Single(chunk.DestinationFiles).Path);
        Assert.Equal(OperationKind.Processed, Assert.Single(chunk.SourceOperations).Kind);
        VirtualFileOperation destOp = Assert.Single(chunk.DestinationOperations);
        Assert.Equal(0, destOp.SourceIndex);
        Assert.Equal(0, destOp.SubjectIndex);
    }

    [Fact]
    public void GlobalSettings_round_trips_its_fields_over_the_wire()
    {
        byte[] wire = IpcSerializer.SerializeResponse(new SettingsResponse
        {
            Settings = new GlobalSettings
            {
                ServiceStartupMode = ServiceStartupMode.RunOnStartup,
                DryRunConcurrencyMode = ConcurrencyMode.Manual,
                DryRunManualWorkers = 6,
            },
        });
        Assert.True(IpcSerializer.DeserializeResponse(wire).TryGetValue(out IpcResponse? reparsed));
        SettingsResponse roundTripped = Assert.IsType<SettingsResponse>(reparsed);
        Assert.Equal(ServiceStartupMode.RunOnStartup, roundTripped.Settings.ServiceStartupMode);
        Assert.Equal(ConcurrencyMode.Manual, roundTripped.Settings.DryRunConcurrencyMode);
        Assert.Equal(6, roundTripped.Settings.DryRunManualWorkers);

        // Enums serialize as strings, consistent with the rest of the wire format.
        using JsonDocument document = JsonDocument.Parse(wire);
        Assert.Equal("Manual",
            document.RootElement.GetProperty("Settings").GetProperty("DryRunConcurrencyMode").GetString());
        Assert.Equal("RunOnStartup",
            document.RootElement.GetProperty("Settings").GetProperty("ServiceStartupMode").GetString());
    }

    [Fact]
    public void Profile_without_a_concurrency_field_deserializes_to_the_default()
    {
        // Mimic a profile file written before the Concurrency field existed: serialize, then strip
        // the property so it is absent (not null) from the JSON.
        string json = Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(
            SampleProfile(), FileManagerJsonContext.Default.Profile));
        JsonObject obj = JsonNode.Parse(json)!.AsObject();
        Assert.True(obj.Remove("Concurrency"));

        Profile? parsed = JsonSerializer.Deserialize(obj.ToJsonString(), FileManagerJsonContext.Default.Profile);
        Assert.NotNull(parsed);
        Assert.NotNull(parsed!.Concurrency);                                 // must not be null (the bug)
        Assert.Equal(ConcurrencyMode.Inherit, parsed.Concurrency.Mode);
    }

    [Fact]
    public void Malformed_and_unknown_discriminator_payloads_deserialize_to_failures()
    {
        Assert.True(IpcSerializer.DeserializeRequest("not json"u8.ToArray()).TryGetError(out string? malformed));
        Assert.Contains("malformed request", malformed);
        Assert.True(IpcSerializer.DeserializeRequest("""{"type":"no-such-request"}"""u8.ToArray()).TryGetError(out string? unknown));
        Assert.Contains("malformed request", unknown);
    }

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

    /// <summary>A minimal report exercising all four collections: one source file overwritten at one
    /// destination, with the destination op referencing both by index.</summary>
    private static DryRunReport SampleReport() => new()
    {
        ProfileId = SomeId,
        GeneratedAt = DateTimeOffset.UnixEpoch,
        SourceFiles = [SampleSourceFile()],
        DestinationFiles = [SampleDestinationFile()],
        SourceOperations = [SampleSourceOp()],
        DestinationOperations =
        [
            new VirtualFileOperation
            {
                Path = @"C:\t\one.txt",
                Root = @"C:\t",
                Kind = OperationKind.Overwrite,
                SourceIndex = 0,
                SubjectIndex = 0,
                Detail = "existing file last modified 1970-01-01",
            },
        ],
    };

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
