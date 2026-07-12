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
        { new DryRunResponse { Report = new DryRunReport(SomeId, DateTimeOffset.UnixEpoch, []) }, "dry-run-report" },
        { new DryRunChunkResponse { Files = [new DryRunFileResult { SourcePath = @"C:\x", Disposition = DryRunFileDisposition.WouldProcess }] }, "dry-run-chunk" },
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
    public void DryRunReport_truncated_flag_round_trips_and_is_false_when_absent()
    {
        byte[] wire = IpcSerializer.SerializeResponse(new DryRunResponse
        {
            Report = new DryRunReport(SomeId, DateTimeOffset.UnixEpoch, [], Truncated: true),
        });
        using JsonDocument document = JsonDocument.Parse(wire);
        Assert.True(document.RootElement.GetProperty("Report").GetProperty("Truncated").GetBoolean());

        Assert.True(IpcSerializer.DeserializeResponse(wire).TryGetValue(out IpcResponse? reparsed));
        DryRunResponse roundTripped = Assert.IsType<DryRunResponse>(reparsed);
        Assert.True(roundTripped.Report.Truncated);

        // Old-server → new-client compatibility: a report serialized before the flag existed
        // (no "Truncated" property) deserializes to the constructor default, false.
        string legacy = """{"type":"dry-run-report","Report":{"ProfileId":""" + $"\"{SomeId}\"" +
            ""","GeneratedAt":"1970-01-01T00:00:00+00:00","Files":[]}}""";
        Assert.True(IpcSerializer.DeserializeResponse(Encoding.UTF8.GetBytes(legacy)).TryGetValue(out IpcResponse? legacyResponse));
        DryRunResponse legacyParsed = Assert.IsType<DryRunResponse>(legacyResponse);
        Assert.False(legacyParsed.Report.Truncated);
    }

    [Fact]
    public void DryRunFileResult_source_root_round_trips_and_is_null_when_absent()
    {
        byte[] wire = IpcSerializer.SerializeResponse(new DryRunResponse
        {
            Report = new DryRunReport(SomeId, DateTimeOffset.UnixEpoch,
            [
                new DryRunFileResult
                {
                    SourcePath = @"C:\a\one.txt",
                    SourceRoot = @"C:\a",
                    Disposition = DryRunFileDisposition.WouldProcess,
                },
            ]),
        });

        Assert.True(IpcSerializer.DeserializeResponse(wire).TryGetValue(out IpcResponse? reparsed));
        DryRunResponse roundTripped = Assert.IsType<DryRunResponse>(reparsed);
        Assert.Equal(@"C:\a", roundTripped.Report.Files[0].SourceRoot);

        // Old-server → new-client compatibility: a result serialized before SourceRoot existed
        // (no "SourceRoot" property) deserializes to null.
        string legacy = """{"type":"dry-run-report","Report":{"ProfileId":""" + $"\"{SomeId}\"" +
            ""","GeneratedAt":"1970-01-01T00:00:00+00:00","Files":[""" +
            """{"SourcePath":"C:\\a\\one.txt","Disposition":"WouldProcess"}]}}""";
        Assert.True(IpcSerializer.DeserializeResponse(Encoding.UTF8.GetBytes(legacy)).TryGetValue(out IpcResponse? legacyResponse));
        DryRunResponse legacyParsed = Assert.IsType<DryRunResponse>(legacyResponse);
        Assert.Null(legacyParsed.Report.Files[0].SourceRoot);
    }

    [Fact]
    public void DryRunReport_destinations_round_trip_and_default_to_empty_when_absent()
    {
        byte[] wire = IpcSerializer.SerializeResponse(new DryRunResponse
        {
            Report = new DryRunReport(SomeId, DateTimeOffset.UnixEpoch, [], Truncated: false,
                Destinations:
                [
                    new DryRunDestinationEntry { TargetPath = @"C:\t\orphan.txt", TargetRoot = @"C:\t", Disposition = DryRunDestinationDisposition.Deleted },
                ]),
        });

        Assert.True(IpcSerializer.DeserializeResponse(wire).TryGetValue(out IpcResponse? reparsed));
        DryRunResponse roundTripped = Assert.IsType<DryRunResponse>(reparsed);
        DryRunDestinationEntry entry = Assert.Single(roundTripped.Report.Destinations!);
        Assert.Equal(@"C:\t\orphan.txt", entry.TargetPath);
        Assert.Equal(DryRunDestinationDisposition.Deleted, entry.Disposition);

        // Old-server → new-client compatibility: a report serialized before Destinations existed
        // (no "Destinations" property) deserializes to the constructor default (null).
        string legacy = """{"type":"dry-run-report","Report":{"ProfileId":""" + $"\"{SomeId}\"" +
            ""","GeneratedAt":"1970-01-01T00:00:00+00:00","Files":[]}}""";
        Assert.True(IpcSerializer.DeserializeResponse(Encoding.UTF8.GetBytes(legacy)).TryGetValue(out IpcResponse? legacyResponse));
        DryRunResponse legacyParsed = Assert.IsType<DryRunResponse>(legacyResponse);
        Assert.Null(legacyParsed.Report.Destinations);
    }

    [Fact]
    public void DryRunTargetAction_target_root_round_trips_and_is_null_when_absent()
    {
        byte[] wire = IpcSerializer.SerializeResponse(new DryRunResponse
        {
            Report = new DryRunReport(SomeId, DateTimeOffset.UnixEpoch,
            [
                new DryRunFileResult
                {
                    SourcePath = @"C:\a\one.txt",
                    Disposition = DryRunFileDisposition.WouldProcess,
                    Targets = [new DryRunTargetAction { TargetPath = @"C:\t\one.txt", TargetRoot = @"C:\t", Kind = DryRunTargetKind.WouldWrite }],
                },
            ]),
        });

        Assert.True(IpcSerializer.DeserializeResponse(wire).TryGetValue(out IpcResponse? reparsed));
        DryRunResponse roundTripped = Assert.IsType<DryRunResponse>(reparsed);
        Assert.Equal(@"C:\t", roundTripped.Report.Files[0].Targets[0].TargetRoot);

        // Old-server → new-client: a target action without TargetRoot deserializes to null.
        string legacy = """{"type":"dry-run-report","Report":{"ProfileId":""" + $"\"{SomeId}\"" +
            ""","GeneratedAt":"1970-01-01T00:00:00+00:00","Files":[{"SourcePath":"C:\\a\\one.txt","Disposition":"WouldProcess","Targets":[{"TargetPath":"C:\\t\\one.txt","Kind":"WouldWrite"}]}]}}""";
        Assert.True(IpcSerializer.DeserializeResponse(Encoding.UTF8.GetBytes(legacy)).TryGetValue(out IpcResponse? legacyResponse));
        DryRunResponse legacyParsed = Assert.IsType<DryRunResponse>(legacyResponse);
        Assert.Null(legacyParsed.Report.Files[0].Targets[0].TargetRoot);
    }

    [Fact]
    public void DryRunDestinationChunkResponse_round_trips()
    {
        byte[] wire = IpcSerializer.SerializeResponse(new DryRunDestinationChunkResponse
        {
            Entries =
            [
                new DryRunDestinationEntry { TargetPath = @"C:\t\keep.txt", TargetRoot = @"C:\t", Disposition = DryRunDestinationDisposition.Untouched },
            ],
        });

        Assert.True(IpcSerializer.DeserializeResponse(wire).TryGetValue(out IpcResponse? reparsed));
        DryRunDestinationChunkResponse chunk = Assert.IsType<DryRunDestinationChunkResponse>(reparsed);
        Assert.Equal(@"C:\t\keep.txt", Assert.Single(chunk.Entries).TargetPath);
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
