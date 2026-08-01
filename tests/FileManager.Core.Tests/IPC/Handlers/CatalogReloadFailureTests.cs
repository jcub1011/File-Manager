using FileManager.Contracts.IPC;
using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using FileManager.Core.IPC.Handlers;
using FileManager.Core.Profiles;
using FileManager.Core.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileManager.Core.Tests.IPC.Handlers;

/// <summary>The profile store mutated on disk but the catalog could not be reloaded. ProfileCatalog
/// leaves its snapshot un-swapped and notifies nobody on that path, so the engine keeps serving the
/// PRE-mutation profile set: a manual run resolves the stale profile from <c>catalog.All</c> and runs
/// it under the old policies (up to a stale <c>PermanentDelete</c>), and no client learns anything.
/// Reporting success there is the failure mode these tests exist to prevent.</summary>
public sealed class CatalogReloadFailureTests
{
    private sealed class FailingReloadCatalog : IProfileCatalog
    {
        public IReadOnlyList<Profile> All { get; } = [];
        public IReadOnlyList<Profile> Active { get; } = [];
        public IDisposable Subscribe(Action changeHandler) => new Noop();
        public Result Reload() => "the profiles directory is no longer readable";
        private sealed class Noop : IDisposable { public void Dispose() { } }
    }

    /// <summary>Accepts every mutation, so the only thing under test is the reload that follows.</summary>
    private sealed class AcceptingStore : IProfileStore
    {
        public Result<IReadOnlyList<Profile>, string> LoadAll() =>
            Result<IReadOnlyList<Profile>, string>.Success([]);

        public Result<Profile, string> Load(Guid profileId) => "not used";

        public Result<IReadOnlyList<ValidationIssue>, string> Save(Profile profile, bool acknowledgeWarnings) =>
            Result<IReadOnlyList<ValidationIssue>, string>.Success([]);

        public Result Delete(Guid profileId) => Result.Success();
    }

    [Fact]
    public async Task Save_fails_loud_when_the_catalog_cannot_be_reloaded()
    {
        SaveProfileHandler handler = new(
            NullLogger<SaveProfileHandler>.Instance, new AcceptingStore(), new FailingReloadCatalog());

        IpcResponse response = await handler.HandleAsync(
            new SaveProfileRequest { Profile = TestProfiles.Valid(), AcknowledgeWarnings = false });

        ErrorResponse error = Assert.IsType<ErrorResponse>(response);
        Assert.Equal("PROFILE_SAVE_FAILED", error.Code);
        Assert.Contains("saved", error.Message);                   // the disk state the user is actually in
        Assert.Contains("no longer readable", error.Message);       // ...and why the list is stale
    }

    [Fact]
    public async Task Delete_fails_loud_when_the_catalog_cannot_be_reloaded()
    {
        DeleteProfileHandler handler = new(
            NullLogger<DeleteProfileHandler>.Instance, new AcceptingStore(), new FailingReloadCatalog());

        IpcResponse response = await handler.HandleAsync(new DeleteProfileRequest { ProfileId = Guid.NewGuid() });

        ErrorResponse error = Assert.IsType<ErrorResponse>(response);
        Assert.Equal("PROFILE_DELETE_FAILED", error.Code);
        Assert.Contains("deleted", error.Message);
    }

    [Fact]
    public async Task Save_reports_the_validation_issues_when_the_reload_succeeds()
    {
        // The happy path is unchanged: a save still answers with ValidationResponse, not OkResponse,
        // because the wire carries issues rather than a saved flag.
        SaveProfileHandler handler = new(
            NullLogger<SaveProfileHandler>.Instance, new AcceptingStore(), new FakeProfileCatalog());

        IpcResponse response = await handler.HandleAsync(
            new SaveProfileRequest { Profile = TestProfiles.Valid(), AcknowledgeWarnings = false });

        Assert.IsType<ValidationResponse>(response);
    }

    [Fact]
    public async Task Delete_reports_Ok_when_the_reload_succeeds()
    {
        DeleteProfileHandler handler = new(
            NullLogger<DeleteProfileHandler>.Instance, new AcceptingStore(), new FakeProfileCatalog());

        IpcResponse response = await handler.HandleAsync(new DeleteProfileRequest { ProfileId = Guid.NewGuid() });

        Assert.IsType<OkResponse>(response);
    }
}
