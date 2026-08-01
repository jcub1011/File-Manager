using FileManager.UI.Services;
using Microsoft.Extensions.Time.Testing;

namespace FileManager.UI.Tests;

/// <summary>The guard that stops an executable which starts but never serves — the classic case being
/// a setting pointing at some unrelated program — from being re-launched on every 2 s status poll.
///
/// Without it each poll spawns another process and holds the connect gate for the launcher's full
/// ~5 s retry budget, which starves every other request. The settings load queues behind it, so the
/// one window that can fix the path never opens: the app locks the user out of its own escape hatch.
///
/// Only the policy is covered here. The path that calls it opens real named pipes and spawns real
/// processes, which is exactly what a test must not do.</summary>
public sealed class IpcGatewayStartCooldownTests
{
    private const string Exe = @"D:\tools\FileManager.Service.exe";

    private static (IpcGateway Gateway, FakeTimeProvider Time) New()
    {
        FakeTimeProvider time = new();
        return (new IpcGateway(() => Exe, time), time);
    }

    [Fact]
    public void A_first_attempt_is_always_allowed()
    {
        (IpcGateway gateway, _) = New();

        Assert.True(gateway.MayStart(Exe));
    }

    [Fact]
    public void The_same_executable_is_not_relaunched_immediately_after_a_failed_start()
    {
        (IpcGateway gateway, FakeTimeProvider time) = New();

        gateway.RecordFailedStart(Exe);
        time.Advance(TimeSpan.FromSeconds(2));      // the status poll comes back around

        Assert.False(gateway.MayStart(Exe));
    }

    [Fact]
    public void The_same_executable_is_retried_once_the_cooldown_expires()
    {
        // A service that crashed once must recover on its own, without the user restarting the app.
        (IpcGateway gateway, FakeTimeProvider time) = New();

        gateway.RecordFailedStart(Exe);
        time.Advance(TimeSpan.FromMinutes(2));

        Assert.True(gateway.MayStart(Exe));
    }

    [Fact]
    public void Correcting_the_path_retries_at_once_rather_than_serving_out_the_cooldown()
    {
        // The recovery path: the user fixes the setting, and the next poll must act on it — making
        // them wait out a cooldown earned by the OLD path would look like the fix did not take.
        (IpcGateway gateway, FakeTimeProvider time) = New();

        gateway.RecordFailedStart(Exe);
        time.Advance(TimeSpan.FromSeconds(1));

        Assert.True(gateway.MayStart(@"D:\correct\FileManager.Service.exe"));
    }

    [Fact]
    public void The_cooldown_is_case_insensitive_like_the_filesystem()
    {
        (IpcGateway gateway, FakeTimeProvider time) = New();

        gateway.RecordFailedStart(Exe);
        time.Advance(TimeSpan.FromSeconds(1));

        Assert.False(gateway.MayStart(Exe.ToUpperInvariant()));
    }

    [Fact]
    public void A_blank_path_is_tracked_like_any_other()
    {
        // Blank still resolves to something (the env override, or the copy beside the app), and that
        // something can fail to start just as readily.
        (IpcGateway gateway, FakeTimeProvider time) = New();

        gateway.RecordFailedStart(null);
        time.Advance(TimeSpan.FromSeconds(1));

        Assert.False(gateway.MayStart(null));
    }
}
