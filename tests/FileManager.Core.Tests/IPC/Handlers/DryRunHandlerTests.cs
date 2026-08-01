using FileManager.Contracts.DryRun;
using FileManager.Contracts.IPC;
using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using FileManager.Core.DryRun;
using FileManager.Core.IPC.Handlers;
using FileManager.Core.Observability;
using FileManager.Core.Profiles;
using FileManager.Core.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileManager.Core.Tests.IPC.Handlers;

public sealed class DryRunHandlerTests
{
    private sealed class FakeCatalog(params Profile[] profiles) : IProfileCatalog
    {
        public IReadOnlyList<Profile> All { get; } = profiles;
        public IReadOnlyList<Profile> Active => All.Where(p => p.Active).ToList();
        public IDisposable Subscribe(Action changeHandler) => throw new NotSupportedException();
        public Result Reload() => Result.Success();
    }

    /// <summary>Never simulates: the not-found guard must short-circuit before the engine is touched.</summary>
    private sealed class ThrowingEngine : IDryRunEngine
    {
        public Task<Result<DryRunReport, string>> SimulateAsync(
            Profile profile, string? scopePath, CancellationToken ct = default) =>
            throw new NotSupportedException("the engine must not be reached for a missing profile");

        public IAsyncEnumerable<Result<DryRunChunk, string>> SimulateStreamAsync(
            Profile profile, string? scopePath, DryRunProgressCounters? progress = null,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    [Fact]
    public async Task Unknown_profile_is_a_PROFILE_NOT_FOUND_error()
    {
        DryRunHandler handler = new(
            NullLogger<DryRunHandler>.Instance, new ThrowingEngine(), new FakeCatalog(),
            NullMemoryTrimCoordinator.Instance);

        IpcResponse response = await handler.HandleAsync(
            new DryRunRequest { ProfileId = Guid.NewGuid(), ScopePath = null });

        ErrorResponse error = Assert.IsType<ErrorResponse>(response);
        Assert.Equal("PROFILE_NOT_FOUND", error.Code);
    }
}
