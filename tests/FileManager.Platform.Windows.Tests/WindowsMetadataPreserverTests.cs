using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using FileManager.Core.Platform;
using FileManager.Platform.Windows;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileManager.Platform.Windows.Tests;

public sealed class WindowsMetadataPreserverTests : IDisposable
{
    private readonly string _dir;
    private readonly WindowsMetadataPreserver _preserver = new(NullLogger<WindowsMetadataPreserver>.Instance);

    public WindowsMetadataPreserverTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "fm-meta-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Apply_copies_the_last_write_time_to_the_target()
    {
        string source = Path.Combine(_dir, "src.txt");
        string dest = Path.Combine(_dir, "dst.txt");
        File.WriteAllText(source, "s");
        File.WriteAllText(dest, "d");
        var when = new DateTime(2021, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(source, when);

        Result result = _preserver.Apply(source, dest, MetadataOnConflict.WarnAndContinue);

        Assert.True(result.IsSuccess);
        Assert.Equal(when, File.GetLastWriteTimeUtc(dest));
    }

    [Fact]
    public void Inspect_reports_no_loss_within_the_same_volume()
    {
        string source = Path.Combine(_dir, "src.txt");
        File.WriteAllText(source, "s");

        Result<MetadataLossReport, string> result = _preserver.Inspect(source, _dir);
        Assert.True(result.TryGetValue(out MetadataLossReport? report));
        Assert.False(report.LossDetected);
    }
}
