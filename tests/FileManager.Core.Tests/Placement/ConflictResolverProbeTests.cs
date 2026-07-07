using FileManager.Contracts.Profiles;
using FileManager.Core.Placement;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileManager.Core.Tests.Placement;

public sealed class ConflictResolverProbeTests : IDisposable
{
    private readonly string _dir;
    private readonly ConflictResolver _resolver = new(NullLogger<ConflictResolver>.Instance);

    public ConflictResolverProbeTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "fm-conflict-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Existing(string name)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllText(path, "existing");
        return path;
    }

    private ConflictOutcome Probe(string path, ConflictResolution policy, DateTimeOffset? incoming = null)
    {
        var probed = _resolver.Probe(path, policy, incoming ?? DateTimeOffset.UtcNow);
        Assert.True(probed.TryGetValue(out ConflictOutcome? outcome));
        return outcome;
    }

    [Fact]
    public void Absent_target_writes_regardless_of_policy()
    {
        string path = Path.Combine(_dir, "new.txt");
        foreach (ConflictResolution policy in new[]
            { ConflictResolution.Skip, ConflictResolution.Overwrite, ConflictResolution.OverwriteIfNewer, ConflictResolution.RenameSuffix })
        {
            ConflictOutcome outcome = Probe(path, policy);
            Assert.Equal(ConflictAction.Write, outcome.Action);
            Assert.Equal(path, outcome.FinalPath);
        }
    }

    [Fact]
    public void Skip_keeps_the_existing_file() =>
        Assert.Equal(ConflictAction.SkipExistingKept, Probe(Existing("kept.txt"), ConflictResolution.Skip).Action);

    [Fact]
    public void Overwrite_always_writes() =>
        Assert.Equal(ConflictAction.Write, Probe(Existing("clobber.txt"), ConflictResolution.Overwrite).Action);

    [Fact]
    public void OverwriteIfNewer_compares_last_write_times()
    {
        string path = Existing("timed.txt");
        DateTimeOffset existingMtime = File.GetLastWriteTimeUtc(path);

        Assert.Equal(ConflictAction.Write,
            Probe(path, ConflictResolution.OverwriteIfNewer, existingMtime.AddMinutes(5)).Action);
        Assert.Equal(ConflictAction.SkipExistingKept,
            Probe(path, ConflictResolution.OverwriteIfNewer, existingMtime.AddMinutes(-5)).Action);
    }

    [Fact]
    public void RenameSuffix_predicts_the_first_free_candidate()
    {
        Existing("song.flac");
        Existing("song (1).flac");

        ConflictOutcome outcome = Probe(Path.Combine(_dir, "song.flac"), ConflictResolution.RenameSuffix);

        Assert.Equal(ConflictAction.Write, outcome.Action);
        Assert.Equal(Path.Combine(_dir, "song (2).flac"), outcome.FinalPath);
    }

    [Fact]
    public void Resolve_is_not_part_of_this_slice() =>
        Assert.Throws<NotSupportedException>(() => _resolver.Resolve(
            Path.Combine(_dir, "x"), ConflictResolution.Skip, null!, 0, Guid.NewGuid(), null!));
}
