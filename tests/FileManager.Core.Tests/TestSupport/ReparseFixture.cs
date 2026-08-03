using System.Diagnostics;

namespace FileManager.Core.Tests.TestSupport;

/// <summary>Creates real directory junctions for the tests that pin the never-descend-a-reparse-point
/// guards (<c>SourceScanner</c>'s and <c>DestinationProjector</c>'s <c>OnSubdirectory</c>), and FAILS
/// LOUDLY when it cannot.
///
/// <para>This exists because the original junction test did the opposite: it shelled out to
/// <c>mklink</c> and <c>return</c>ed — reporting a pass — whenever creation failed. Those guards are
/// what stops an unbounded re-walk and a scope escape, and a green test that never ran is worse than no
/// test at all, because it reads as coverage. Junctions need no elevation, so failure here means the
/// environment is genuinely unable to exercise the guard and that is worth knowing.</para>
///
/// <para>Set <see cref="SkipEnvVar"/> to <c>1</c> to opt out on an environment that truly cannot (a
/// non-NTFS work directory, a locked-down container). Opt-in only, and by design noisy to arrange.</para></summary>
internal static class ReparseFixture
{
    public const string SkipEnvVar = "FILEMANAGER_TESTS_SKIP_REPARSE";

    public static bool SkipRequested =>
        Environment.GetEnvironmentVariable(SkipEnvVar) is "1" or "true" or "TRUE";

    /// <summary>Creates a directory junction at <paramref name="linkPath"/> pointing at
    /// <paramref name="targetPath"/>. Returns null only when <see cref="SkipEnvVar"/> asked to skip;
    /// otherwise it either succeeds or fails the test.</summary>
    public static Junction? CreateJunction(string linkPath, string targetPath)
    {
        if (SkipRequested)
            return null;

        // mklink is a cmd builtin, not an executable, and .NET has no junction API (only
        // CreateSymbolicLink, which needs Developer Mode or elevation — a junction needs neither).
        using Process mklink = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c mklink /J \"{linkPath}\" \"{targetPath}\"",
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        string stdout = mklink.StandardOutput.ReadToEnd();
        string stderr = mklink.StandardError.ReadToEnd();
        mklink.WaitForExit();

        Assert.True(
            mklink.ExitCode == 0 && Directory.Exists(linkPath),
            $"Could not create the directory junction this test needs.\n"
            + $"  link:   {linkPath}\n  target: {targetPath}\n"
            + $"  mklink exit {mklink.ExitCode}: {stdout.Trim()} {stderr.Trim()}\n"
            + $"Junctions require no elevation, so this is an environment problem worth fixing rather "
            + $"than skipping. Set {SkipEnvVar}=1 to skip these tests deliberately.");

        // The guards read FileAttributes.ReparsePoint off the enumeration record and nothing else, so a
        // "junction" without that flag would make them pass for the wrong reason. Pin the premise here,
        // once, rather than in every test that uses a junction.
        FileAttributes attributes = File.GetAttributes(linkPath);
        Assert.True(
            (attributes & FileAttributes.ReparsePoint) != 0,
            $"The junction at {linkPath} was created but carries no ReparsePoint attribute ({attributes}); "
            + "the guards under test key off exactly that flag, so this fixture proves nothing.");

        return new Junction(linkPath);
    }

    /// <summary>Owns the junction's lifetime. Removing it before the enclosing fixture's recursive
    /// delete matters: <c>Directory.Delete(recursive: true)</c> over a tree containing a junction whose
    /// target is already gone throws, so a test that skipped this would fail in teardown instead.</summary>
    internal sealed class Junction(string path) : IDisposable
    {
        public string Path { get; } = path;

        public void Dispose()
        {
            try
            {
                // recursive: false — deleting a junction must never touch what it points at.
                Directory.Delete(Path, recursive: false);
            }
            catch (DirectoryNotFoundException)
            {
                // Already gone (or its target is), which is the outcome we wanted.
            }
        }
    }
}
