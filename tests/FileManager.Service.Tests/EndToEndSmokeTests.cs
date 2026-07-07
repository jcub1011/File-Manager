using FileManager.Contracts.DryRun;
using FileManager.Contracts.IPC;
using FileManager.Contracts.Profiles;
using FileManager.Core;
using FileManager.Core.DryRun;
using FileManager.Core.Files;
using FileManager.Core.Filtering;
using FileManager.Core.IPC;
using FileManager.Core.IPC.Handlers;
using FileManager.Core.Placement;
using FileManager.Core.Profiles;
using FileManager.Core.Watching;
using FileManager.Platform.Windows;
using Microsoft.Extensions.Logging.Abstractions;
using System.Runtime.Versioning;

namespace FileManager.Service.Tests;

/// <summary>The whole slice over a real named pipe: the real Core graph behind IpcServer +
/// WindowsIpcEndpointProvider, driven by the real Contracts IpcClient — save, list, get,
/// dry-run over a real temp tree, delete, NOT_IMPLEMENTED, and version rejection.</summary>
[SupportedOSPlatform("windows")]
public sealed class EndToEndSmokeTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fm-e2e-" + Guid.NewGuid().ToString("N"));
    private IpcServer? _server;

    private string SourceDir => Path.Combine(_root, "source");
    private string TargetDir => Path.Combine(_root, "target");

    public Task InitializeAsync()
    {
        Environment.SetEnvironmentVariable("FILEMANAGER_PIPE_NAME", "fm-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(SourceDir);
        Directory.CreateDirectory(TargetDir);

        // The same graph Program.cs composes, pointed at a temp EnginePaths.
        EnginePaths paths = new() { Root = Path.Combine(_root, "engine") };
        FileSystemService fileSystem = new(NullLogger<FileSystemService>.Instance);
        FilterCompiler filterCompiler = new(NullLogger<FilterCompiler>.Instance, TimeProvider.System);
        ProfileValidator validator = new(NullLogger<ProfileValidator>.Instance, filterCompiler);
        ProfileStore store = new(NullLogger<ProfileStore>.Instance, paths, validator);
        ProfileCatalog catalog = new(NullLogger<ProfileCatalog>.Instance, store);
        SourceScanner scanner = new(NullLogger<SourceScanner>.Instance, fileSystem, TimeProvider.System);
        FileHasher hasher = new(NullLogger<FileHasher>.Instance);
        ConflictResolver resolver = new(NullLogger<ConflictResolver>.Instance);
        DryRunEngine dryRun = new(NullLogger<DryRunEngine>.Instance, catalog, scanner, filterCompiler, hasher, resolver, TimeProvider.System);

        IIpcRequestHandler[] handlers =
        [
            new GetStatusHandler(catalog),
            new ListProfilesHandler(catalog),
            new GetProfileHandler(catalog),
            new SaveProfileHandler(NullLogger<SaveProfileHandler>.Instance, store, catalog),
            new DeleteProfileHandler(NullLogger<DeleteProfileHandler>.Instance, store, catalog),
            new ValidateProfileHandler(validator, catalog),
            new DryRunHandler(dryRun, catalog),
        ];
        _server = new IpcServer(
            NullLogger<IpcServer>.Instance,
            new WindowsIpcEndpointProvider(),
            handlers.ToDictionary(h => h.RequestType));

        Assert.True(_server.Start().IsSuccess);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_server is not null)
            await _server.StopAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
        Environment.SetEnvironmentVariable("FILEMANAGER_PIPE_NAME", null);
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Full_profile_lifecycle_and_dry_run_over_the_pipe()
    {
        File.WriteAllText(Path.Combine(SourceDir, "fresh.txt"), "new content");
        File.WriteAllText(Path.Combine(SourceDir, "same.txt"), "identical");
        File.WriteAllText(Path.Combine(TargetDir, "same.txt"), "identical");

        var connected = await IpcClient.ConnectAsync();
        Assert.True(connected.TryGetValue(out IpcClient? client));
        await using (client)
        {
            // status
            var status = await client!.RequestAsync<StatusResponse>(new GetStatusRequest());
            Assert.True(status.TryGetValue(out StatusResponse? statusResponse));
            Assert.Equal(0, statusResponse!.Status.ActiveProfiles);

            // save
            Profile profile = NewProfile();
            var saved = await client.RequestAsync<ValidationResponse>(
                new SaveProfileRequest { Profile = profile, AcknowledgeWarnings = false });
            Assert.True(saved.TryGetValue(out ValidationResponse? validation));
            Assert.Empty(validation!.Issues);

            // list
            var listed = await client.RequestAsync<ProfileListResponse>(new ListProfilesRequest());
            Assert.True(listed.TryGetValue(out ProfileListResponse? list));
            Assert.Single(list!.Profiles);
            Assert.Equal(profile.Id, list.Profiles[0].ProfileId);

            // get
            var got = await client.RequestAsync<ProfileResponse>(new GetProfileRequest { ProfileId = profile.Id });
            Assert.True(got.TryGetValue(out ProfileResponse? getResponse));
            Assert.Equal(profile.Name, getResponse!.Profile.Name);

            // dry run
            var dryRun = await client.RequestAsync<DryRunResponse>(new DryRunRequest { ProfileId = profile.Id });
            Assert.True(dryRun.TryGetValue(out DryRunResponse? dryRunResponse));
            DryRunReport report = dryRunResponse!.Report;
            Assert.Equal(2, report.Files.Count);
            Assert.Contains(report.Files, f =>
                f.SourcePath.EndsWith("fresh.txt") && f.Disposition == DryRunFileDisposition.WouldProcess);
            Assert.Contains(report.Files, f =>
                f.SourcePath.EndsWith("same.txt") && f.Disposition == DryRunFileDisposition.WouldSkipUnchanged);

            // out-of-scope request → NOT_IMPLEMENTED
            var refused = await client.RequestAsync<OkResponse>(
                new RunProfileRequest { ProfileId = profile.Id, Path = SourceDir });
            Assert.True(refused.TryGetError(out IpcError? notImplemented));
            Assert.Equal("NOT_IMPLEMENTED", notImplemented!.Code);

            // protocol version mismatch
            var mismatched = await client.RequestAsync<StatusResponse>(
                new GetStatusRequest { ProtocolVersion = 99 });
            Assert.True(mismatched.TryGetError(out IpcError? versionError));
            Assert.Equal("IPC_VERSION_MISMATCH", versionError!.Code);

            // delete
            var deleted = await client.RequestAsync<OkResponse>(new DeleteProfileRequest { ProfileId = profile.Id });
            Assert.True(deleted.IsSuccess);
            var gone = await client.RequestAsync<ProfileResponse>(new GetProfileRequest { ProfileId = profile.Id });
            Assert.True(gone.TryGetError(out IpcError? notFound));
            Assert.Equal("PROFILE_NOT_FOUND", notFound!.Code);
        }
    }

    private Profile NewProfile() => new()
    {
        SchemaVersion = 2,
        Id = Guid.NewGuid(),
        Name = "e2e",
        Active = true,
        SyncMode = SyncMode.AdditiveArchive,
        TargetLayout = TargetLayout.PreserveStructure,
        Triggers = new TriggerSettings { ManualShell = true, Watcher = false, Schedule = null },
        Sources = [new SourceConfig { Path = SourceDir }],
        Targets = [new TargetConfig { Path = TargetDir }],
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
