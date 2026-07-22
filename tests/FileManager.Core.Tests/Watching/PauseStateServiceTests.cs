using FileManager.Core.Watching;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileManager.Core.Tests.Watching;

public sealed class PauseStateServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fm-pause-" + Guid.NewGuid().ToString("N"));
    private readonly EnginePaths _paths;

    public PauseStateServiceTests()
    {
        _paths = new EnginePaths { Root = _root };
        Directory.CreateDirectory(_paths.StateDirectory);
    }

    [Fact]
    public void Persists_across_a_new_instance()
    {
        PauseStateService first = new(_paths, NullLogger<PauseStateService>.Instance);
        Assert.False(first.IsPaused);
        Assert.True(first.SetPaused(true).IsSuccess);

        PauseStateService reloaded = new(_paths, NullLogger<PauseStateService>.Instance);
        Assert.True(reloaded.IsPaused);
    }

    [Fact]
    public void Notifies_subscribers_on_change()
    {
        PauseStateService service = new(_paths, NullLogger<PauseStateService>.Instance);
        bool? observed = null;
        using IDisposable _ = service.Subscribe(paused => observed = paused);

        service.SetPaused(true);
        Assert.True(observed);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
