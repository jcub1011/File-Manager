using FileManager.Core.Files;

namespace FileManager.Core.Tests.Files;

public sealed class InfrastructurePathsTests
{
    [Fact]
    public void Staged_paths_are_distinct_for_same_named_targets_of_one_job()
    {
        // One job may have two targets under the same root whose layouts resolve to the same file
        // NAME in different directories; their staged priors must never collide.
        var job = Guid.NewGuid();
        string a = InfrastructurePaths.StagedPathFor(@"C:\root", job, 0, "file.txt");
        string b = InfrastructurePaths.StagedPathFor(@"C:\root", job, 1, "file.txt");

        Assert.NotEqual(a, b, StringComparer.OrdinalIgnoreCase);
        Assert.StartsWith(Path.Combine(@"C:\root", ".fm_staging", job.ToString("N")), a);
    }

    [Theory]
    [InlineData("report.pdf.fmtmp-1a2b3c4d", true)]     // the placer's actual shape
    [InlineData("song.flac.fmtmp-abc12345", true)]
    [InlineData("foo.fmtmp-123", true)]
    [InlineData("notes.fmtmp-backup.txt", false)]        // user file that merely contains the marker
    [InlineData("archive.fmtmp-", false)]                 // marker with no suffix
    [InlineData("plain.txt", false)]
    [InlineData("fmtmp-1a2b3c4d", false)]                 // marker requires the leading dot
    public void Temp_file_marker_is_anchored_to_the_placer_shape(string fileName, bool isTemp) =>
        Assert.Equal(isTemp, InfrastructurePaths.IsTempFileName(fileName));
}
