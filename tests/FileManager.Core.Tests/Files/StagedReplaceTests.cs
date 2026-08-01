using FileManager.Core.Files;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileManager.Core.Tests.Files;

/// <summary>The staged-overwrite primitive shared by the live placer and crash recovery. Both used to
/// carry their own copy and only the placer's guarded the two-step fallback, so these pin the guard
/// once for both callers.</summary>
public sealed class StagedReplaceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fm-staged-" + Guid.NewGuid().ToString("N"));

    public StagedReplaceTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static void Execute(string temp, string final, string staged) =>
        StagedReplace.Execute(temp, final, staged, NullLogger.Instance);

    [Fact]
    public void Swaps_the_temp_in_and_parks_the_prior_version_at_staged()
    {
        string final = Path.Combine(_root, "out.dat");
        string temp = final + ".fmtmp-abc";
        string staged = Path.Combine(_root, ".fm_staging", "job", "out.dat");
        File.WriteAllText(final, "PRIOR");
        File.WriteAllText(temp, "NEW");
        Directory.CreateDirectory(Path.GetDirectoryName(staged)!);

        Execute(temp, final, staged);

        Assert.Equal("NEW", File.ReadAllText(final));
        Assert.Equal("PRIOR", File.ReadAllText(staged));   // recoverable for rollback (I-PRIOR)
        Assert.False(File.Exists(temp));
    }

    [Fact]
    public void Completes_rather_than_throwing_when_the_final_name_is_already_absent()
    {
        // The idempotency guard, and the reason it is mandatory: a prior attempt (a placer retry, or a
        // recovery pass replaying the same journal rows on a later startup) that moved the prior version
        // out and then failed before moving the temp in leaves exactly this state. Without the guard the
        // second move is preceded by a File.Move of a file that no longer exists, which throws
        // FileNotFound on every subsequent attempt — stranding the final name absent forever.
        string final = Path.Combine(_root, "out.dat");        // absent: already moved to staging
        string temp = final + ".fmtmp-abc";
        string staged = Path.Combine(_root, ".fm_staging", "job", "out.dat");
        File.WriteAllText(temp, "NEW");
        Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
        File.WriteAllText(staged, "PRIOR");

        Execute(temp, final, staged);

        Assert.Equal("NEW", File.ReadAllText(final));       // placement finished
        Assert.Equal("PRIOR", File.ReadAllText(staged));    // the parked prior version is untouched
        Assert.False(File.Exists(temp));
    }

    [Fact]
    public void Creates_the_staging_directory_on_the_fallback_path()
    {
        // The fallback must not depend on its caller having created the staging directory first: the
        // placer's copy created it, the recovery copy relied on the call site doing so.
        string final = Path.Combine(_root, "out.dat");
        string temp = final + ".fmtmp-abc";
        string staged = Path.Combine(_root, ".fm_staging", "not-created-yet", "out.dat");
        File.WriteAllText(temp, "NEW");

        Execute(temp, final, staged);

        Assert.Equal("NEW", File.ReadAllText(final));
    }
}
