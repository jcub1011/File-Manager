using FileManager.Contracts.IPC;

namespace FileManager.Contracts.Tests.IPC;

/// <summary>Covers the two pieces of <see cref="ServiceLauncher"/> that decide WHERE the service is
/// and WHAT the user is told when it is not there. <c>ConnectOrStartAsync</c> itself is deliberately
/// not exercised: it opens a real named pipe and spawns a process.
///
/// Its own collection, and the environment variable is saved and restored: resolution reads
/// process-wide state, so anything else touching the environment in parallel would make this flaky.</summary>
[Collection(nameof(ServiceLauncherTests))]
[CollectionDefinition(nameof(ServiceLauncherTests), DisableParallelization = true)]
public sealed class ServiceLauncherTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fm-launcher-" + Guid.NewGuid().ToString("N"));

    public ServiceLauncherTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp tree is not worth failing a test over.
        }
    }

    /// <summary>A file that actually exists — resolution only accepts a candidate it can see.</summary>
    private string RealExe(string name)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllText(path, "not really an executable");
        return path;
    }

    private string MissingExe(string name = "gone.exe") => Path.Combine(_dir, "nowhere", name);

    private static void WithEnv(string? value, Action body)
    {
        string? original = Environment.GetEnvironmentVariable(ServiceLauncher.ServiceExeOverrideVariable);
        Environment.SetEnvironmentVariable(ServiceLauncher.ServiceExeOverrideVariable, value);
        try
        {
            body();
        }
        finally
        {
            Environment.SetEnvironmentVariable(ServiceLauncher.ServiceExeOverrideVariable, original);
        }
    }

    // ============================ Precedence ============================

    [Fact]
    public void A_usable_configured_path_outranks_the_environment_override()
    {
        // The precedence that makes "fix it in Settings" true: a setting the user can see and edit must
        // beat an ambient variable they cannot.
        string configured = RealExe("FileManager.Service.exe");
        string env = RealExe("FromEnv.exe");

        WithEnv(env, () =>
        {
            ServiceExeCandidate chosen = Assert.IsType<ServiceExeCandidate>(ServiceLauncher.Resolve(configured).Chosen);
            Assert.Equal(configured, chosen.Path);
            Assert.Equal(ServiceExeSource.Setting, chosen.Source);
        });
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_configured_path_is_not_a_candidate_at_all(string? configured)
    {
        string env = RealExe("FromEnv.exe");

        WithEnv(env, () =>
        {
            ServiceExeResolution resolution = ServiceLauncher.Resolve(configured);

            Assert.Null(resolution.Configured);
            Assert.Equal(env, resolution.Chosen?.Path);
        });
    }

    [Fact]
    public void With_nothing_configured_the_service_is_probed_beside_this_assembly()
    {
        WithEnv(null, () =>
        {
            ServiceExeResolution resolution = ServiceLauncher.Resolve(null);

            ServiceExeCandidate beside = Assert.Single(resolution.Candidates);
            Assert.Equal(ServiceExeSource.BesideApp, beside.Source);
            Assert.Equal(Path.Combine(AppContext.BaseDirectory, ServiceLauncher.ServiceExeName), beside.Path);
        });
    }

    [Fact]
    public void A_configured_path_is_trimmed()
    {
        string configured = RealExe("FileManager.Service.exe");

        WithEnv(null, () =>
            Assert.Equal(configured, ServiceLauncher.Resolve($"  {configured}  ").Chosen?.Path));
    }

    // ============================ Fallback ============================

    [Fact]
    public void A_configured_path_that_is_not_there_falls_back_to_the_next_candidate()
    {
        // The whole point of first-USABLE-wins: a stale setting must not leave the app unable to start
        // its own service.
        string env = RealExe("FromEnv.exe");

        WithEnv(env, () =>
        {
            ServiceExeResolution resolution = ServiceLauncher.Resolve(MissingExe());

            Assert.False(resolution.Configured!.IsUsable);
            Assert.Equal(env, resolution.Chosen?.Path);
            Assert.True(resolution.FellBack);
        });
    }

    [Fact]
    public void A_relative_configured_path_is_never_usable()
    {
        // It would bind to the working directory, which in a real launch is Program Files or System32.
        WithEnv(null, () =>
            Assert.False(ServiceLauncher.Resolve(@"..\FileManager.Service.exe").Configured!.IsUsable));
    }

    [Fact]
    public void A_malformed_configured_path_is_unusable_rather_than_throwing()
    {
        WithEnv(null, () =>
            Assert.False(ServiceLauncher.Resolve("\0:\\nope\\FileManager.Service.exe").Configured!.IsUsable));
    }

    [Fact]
    public void Falling_back_is_not_reported_when_the_configured_path_works()
    {
        string configured = RealExe("FileManager.Service.exe");

        WithEnv(null, () => Assert.False(ServiceLauncher.Resolve(configured).FellBack));
    }

    [Fact]
    public void Nothing_usable_anywhere_leaves_no_choice()
    {
        WithEnv(MissingExe("env.exe"), () =>
        {
            ServiceExeResolution resolution = ServiceLauncher.Resolve(MissingExe("set.exe"));

            Assert.Null(resolution.Chosen);
            Assert.False(resolution.FellBack);      // nothing to fall back TO
            Assert.Equal(3, resolution.Candidates.Count);
        });
    }

    // ============================ Messages ============================

    [Fact]
    public void The_not_found_message_names_every_location_checked()
    {
        WithEnv(MissingExe("env.exe"), () =>
        {
            string message = ServiceLauncher.NotFoundMessage(ServiceLauncher.Resolve(MissingExe("set.exe")));

            Assert.Contains(MissingExe("set.exe"), message);
            Assert.Contains(MissingExe("env.exe"), message);
            Assert.Contains("Settings", message);
            // Callers prefix it ("Disconnected — ", "Dry run failed: "), so a capitalised sentence
            // would read wrong everywhere it is shown.
            Assert.StartsWith("service is not running", message, StringComparison.Ordinal);
            // The environment variable is a developer affordance; naming it in the status bar was the
            // original bug, so it must not come back.
            Assert.DoesNotContain(ServiceLauncher.ServiceExeOverrideVariable, message);
        });
    }
}
