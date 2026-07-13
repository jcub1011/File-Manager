using FileManager.Contracts.Profiles;
using FileManager.Core.Jobs;
using FileManager.Core.Locking;
using FileManager.Core.Placement;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileManager.Core.Tests.Placement;

public sealed class ConflictResolverProbeTests : IDisposable
{
    private readonly string _dir;
    private readonly PathLockRegistry _locks = new();
    private readonly ConflictResolver _resolver;

    public ConflictResolverProbeTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "fm-conflict-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _resolver = new ConflictResolver(_locks, new(), NullLogger<ConflictResolver>.Instance);
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
    public async Task Resolve_skip_keeps_the_existing_file()
    {
        string path = Existing("kept.txt");
        await using PathLockSet held = await _locks.AcquireAsync([], JobId.New());
        var resolved = _resolver.Resolve(path, ConflictResolution.Skip, Sealed(), 0, Guid.NewGuid(), held);
        Assert.True(resolved.TryGetValue(out ConflictOutcome? outcome));
        Assert.Equal(ConflictAction.SkipExistingKept, outcome.Action);
    }

    [Fact]
    public async Task Resolve_rename_suffix_reserves_the_first_free_lockable_candidate()
    {
        Existing("song.flac");
        await using PathLockSet held = await _locks.AcquireAsync([], JobId.New());

        var resolved = _resolver.Resolve(Path.Combine(_dir, "song.flac"), ConflictResolution.RenameSuffix, Sealed(), 0, Guid.NewGuid(), held);

        Assert.True(resolved.TryGetValue(out ConflictOutcome? outcome));
        Assert.Equal(ConflictAction.Write, outcome.Action);
        Assert.Equal(Path.Combine(_dir, "song (1).flac"), outcome.FinalPath);
        // The chosen candidate is now held by this job's lock set (reserved against sibling jobs).
        Assert.Contains(held.Paths, p => p.Value.EndsWith("song (1).flac", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Resolve_overwrite_if_newer_priority_keeps_a_lower_index_placement()
    {
        Guid profileId = Guid.NewGuid();
        string path = Existing("shared.txt");
        var priorities = new SourcePriorityRegistry();
        NormalizedPath.Create(path).TryGetValue(out NormalizedPath key);
        priorities.RecordPlacement(profileId, key, sourceIndex: 0);   // a higher-priority source placed here this session
        var resolver = new ConflictResolver(_locks, priorities, NullLogger<ConflictResolver>.Instance);
        await using PathLockSet held = await _locks.AcquireAsync([], JobId.New());

        // An incoming file from a lower-priority (higher-index) source must keep the existing one.
        var resolved = resolver.Resolve(path, ConflictResolution.Overwrite, Sealed(), sourceIndex: 3, profileId, held);
        Assert.True(resolved.TryGetValue(out ConflictOutcome? outcome));
        Assert.Equal(ConflictAction.SkipExistingKept, outcome.Action);
    }

    [Fact]
    public async Task Resolve_overwrite_higher_priority_source_overwrites_a_lower_priority_placement()
    {
        Guid profileId = Guid.NewGuid();
        string path = Existing("shared.txt");
        var priorities = new SourcePriorityRegistry();
        NormalizedPath.Create(path).TryGetValue(out NormalizedPath key);
        priorities.RecordPlacement(profileId, key, sourceIndex: 5);   // placed this session by a lower-priority source
        var resolver = new ConflictResolver(_locks, priorities, NullLogger<ConflictResolver>.Instance);
        await using PathLockSet held = await _locks.AcquireAsync([], JobId.New());

        // A higher-priority (lower-index) incoming source must overwrite the existing placement.
        var resolved = resolver.Resolve(path, ConflictResolution.Overwrite, Sealed(), sourceIndex: 0, profileId, held);
        Assert.True(resolved.TryGetValue(out ConflictOutcome? outcome));
        Assert.Equal(ConflictAction.Write, outcome.Action);
    }

    [Fact]
    public async Task Resolve_rename_suffix_skips_taken_candidates_and_reserves_the_next_free_name()
    {
        Existing("song.flac");
        Existing("song (1).flac");
        await using PathLockSet held = await _locks.AcquireAsync([], JobId.New());

        var resolved = _resolver.Resolve(Path.Combine(_dir, "song.flac"), ConflictResolution.RenameSuffix, Sealed(), 0, Guid.NewGuid(), held);

        Assert.True(resolved.TryGetValue(out ConflictOutcome? outcome));
        Assert.Equal(ConflictAction.Write, outcome.Action);
        Assert.Equal(Path.Combine(_dir, "song (2).flac"), outcome.FinalPath);   // (1) is taken → next free is (2)
        // The chosen candidate is reserved in this job's lock set (held against sibling jobs).
        Assert.Contains(held.Paths, p => p.Value.EndsWith("song (2).flac", StringComparison.OrdinalIgnoreCase));
    }

    private static SealedOutput Sealed(DateTimeOffset? sourceLastWrite = null) => new()
    {
        Path = "unused",
        SizeBytes = 8,
        ContentHash = "",
        SourceLastWriteUtc = sourceLastWrite ?? DateTimeOffset.UtcNow,
    };
}
