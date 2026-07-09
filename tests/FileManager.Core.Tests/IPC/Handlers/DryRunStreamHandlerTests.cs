using FileManager.Contracts.DryRun;
using FileManager.Contracts.IPC;
using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using FileManager.Core.DryRun;
using FileManager.Core.IPC.Handlers;
using FileManager.Core.Profiles;
using FileManager.Core.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using System.Runtime.CompilerServices;

namespace FileManager.Core.Tests.IPC.Handlers;

public sealed class DryRunStreamHandlerTests
{
    private sealed class FakeCatalog(params Profile[] profiles) : IProfileCatalog
    {
        public IReadOnlyList<Profile> All { get; } = profiles;
        public IReadOnlyList<Profile> Active => All.Where(p => p.Active).ToList();
        public IDisposable Subscribe(Action changeHandler) => throw new NotSupportedException();
        public Result Reload() => Result.Success();
    }

    /// <summary>Yields <paramref name="totalFiles"/> WouldProcess results in fixed-size chunks; the
    /// handler decides where (if anywhere) to truncate.</summary>
    private sealed class FakeStreamEngine(int totalFiles, int chunkSize) : IDryRunEngine
    {
        public Task<Result<DryRunReport, string>> SimulateAsync(
            Guid profileId, string? scopePath, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<Result<IReadOnlyList<DryRunFileResult>, string>> SimulateStreamAsync(
            Guid profileId, string? scopePath, [EnumeratorCancellation] CancellationToken ct = default)
        {
            for (int emitted = 0; emitted < totalFiles;)
            {
                int n = Math.Min(chunkSize, totalFiles - emitted);
                List<DryRunFileResult> files = Enumerable.Range(0, n)
                    .Select(i => new DryRunFileResult
                    {
                        SourcePath = $@"C:\x\{emitted + i}.dat",
                        Disposition = DryRunFileDisposition.WouldProcess,
                    })
                    .ToList();
                emitted += n;
                yield return Result<IReadOnlyList<DryRunFileResult>, string>.Success(files);
                await Task.Yield();
            }
        }
    }

    private static DryRunStreamHandler NewHandler(Profile profile, IDryRunEngine engine, int maxStreamedFiles) =>
        new(NullLogger<DryRunStreamHandler>.Instance, engine, new FakeCatalog(profile), TimeProvider.System)
        { MaxStreamedFiles = maxStreamedFiles };

    private static async Task<List<IpcResponse>> Collect(DryRunStreamHandler handler, Guid profileId)
    {
        List<IpcResponse> frames = [];
        await foreach (IpcResponse frame in handler.HandleStreamAsync(new DryRunStreamRequest { ProfileId = profileId }))
            frames.Add(frame);
        return frames;
    }

    [Fact]
    public async Task Truncates_and_marks_the_completion_when_the_emitted_cap_is_reached()
    {
        Profile profile = TestProfiles.Valid();
        DryRunStreamHandler handler = NewHandler(profile, new FakeStreamEngine(totalFiles: 100, chunkSize: 4), maxStreamedFiles: 8);

        List<IpcResponse> frames = await Collect(handler, profile.Id);

        DryRunCompleteResponse complete = Assert.IsType<DryRunCompleteResponse>(frames[^1]);
        Assert.True(complete.Truncated);
        int emitted = frames.OfType<DryRunChunkResponse>().Sum(c => c.Files.Count);
        Assert.Equal(8, emitted);   // stops at the cap rather than forwarding all 100
    }

    [Fact]
    public async Task Does_not_mark_the_completion_when_the_report_fits_under_the_cap()
    {
        Profile profile = TestProfiles.Valid();
        DryRunStreamHandler handler = NewHandler(profile, new FakeStreamEngine(totalFiles: 8, chunkSize: 4), maxStreamedFiles: 500);

        List<IpcResponse> frames = await Collect(handler, profile.Id);

        DryRunCompleteResponse complete = Assert.IsType<DryRunCompleteResponse>(frames[^1]);
        Assert.False(complete.Truncated);
        Assert.Equal(8, frames.OfType<DryRunChunkResponse>().Sum(c => c.Files.Count));
    }

    [Fact]
    public async Task Unknown_profile_is_a_single_PROFILE_NOT_FOUND_frame()
    {
        Profile profile = TestProfiles.Valid();
        DryRunStreamHandler handler = NewHandler(profile, new FakeStreamEngine(totalFiles: 4, chunkSize: 4), maxStreamedFiles: 500);

        List<IpcResponse> frames = await Collect(handler, Guid.NewGuid());

        ErrorResponse error = Assert.IsType<ErrorResponse>(Assert.Single(frames));
        Assert.Equal("PROFILE_NOT_FOUND", error.Code);
    }
}
