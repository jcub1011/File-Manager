using FileManager.Contracts.Primitives;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FileManager.Contracts.IPC;

public static class ServiceLauncher
{
    /// <summary>Environment override for the service executable path — F5 development, where
    /// the UI and the Service publish to different directories. Outranked by the user's setting;
    /// see <see cref="Resolve"/>.</summary>
    public const string ServiceExeOverrideVariable = "FILEMANAGER_SERVICE_EXE";

    /// <summary>The file name a normal install lays down beside the app. Also what the settings window
    /// checks a chosen file against before warning that it looks like the wrong program.</summary>
    public const string ServiceExeName = "FileManager.Service.exe";

    private const int RetryCount = 20;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>Connect; on failure start the service executable and retry with backoff
    /// (250 ms × 20 ≈ 5 s budget, §3.3). Concurrent callers may both spawn a process; the
    /// service's single-instance mutex makes the loser exit immediately, so the race is benign.</summary>
    /// <param name="serviceExePath">The executable path configured by the user, or null/blank for
    /// none. Passed in rather than read here so this layer keeps no knowledge of where the UI stores
    /// its settings.</param>
    /// <param name="allowStart">False suppresses the start attempt, leaving a plain connect. The
    /// caller sets this while an executable that failed to bring up a service is still configured:
    /// without it, an exe that starts happily but never serves is re-launched on every status poll
    /// forever, and each attempt burns the full retry budget holding the connect gate.</param>
    public static async Task<Result<IpcClient, string>> ConnectOrStartAsync(
        string? serviceExePath = null, bool allowStart = true, CancellationToken ct = default)
    {
        Result<IpcClient, string> first = await IpcClient.ConnectAsync(ct).ConfigureAwait(false);
        if (first.IsSuccess)
            return first;
        if (first.IsCanceled)
            return Result<IpcClient, string>.Canceled();

        ServiceExeResolution resolution = Resolve(serviceExePath);
        if (resolution.Chosen is not { } chosen)
            return NotFoundMessage(resolution);

        if (!allowStart)
            return $"service is not running, and the last attempt to start it from " +
                   $"\"{chosen.Path}\" did not succeed (check the service executable path in Settings)";

        try
        {
            using Process? process = Process.Start(new ProcessStartInfo
            {
                FileName = chosen.Path,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(chosen.Path)!,
            });
            if (process is null)
                return $"failed to start \"{chosen.Path}\"{SettingsHint(chosen)}";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return $"failed to start \"{chosen.Path}\": {ex.Message}{SettingsHint(chosen)}";
        }
        catch (Exception ex)
        {
            // Last resort: an unexpected exception becomes a traceable failure value (callers
            // log every failure).
            return $"failed to start \"{chosen.Path}\": {ex.GetType().Name}: {ex.Message}{SettingsHint(chosen)}";
        }

        string lastError = "unknown";
        for (int attempt = 0; attempt < RetryCount; attempt++)
        {
            try
            {
                await Task.Delay(RetryDelay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return Result<IpcClient, string>.Canceled();
            }
            Result<IpcClient, string> retry = await IpcClient.ConnectAsync(ct).ConfigureAwait(false);
            if (retry.IsSuccess)
                return retry;
            if (retry.IsCanceled)
                return Result<IpcClient, string>.Canceled();
            retry.TryGetError(out lastError!);
        }
        // The file was there and the process started, so the path is not obviously the problem —
        // except when it points at something that simply is not the service, which is exactly what a
        // started-but-never-connected outcome looks like. Say so when the user chose the path.
        return $"\"{chosen.Path}\" started but did not accept a connection within the retry budget: " +
               $"{lastError}{SettingsHint(chosen)}";
    }

    /// <summary>Every place the executable might be, in precedence order, and whether it is there.
    /// <para>The FIRST USABLE candidate wins — not simply the first one named. A configured path that
    /// is missing therefore falls back rather than failing outright, so a stale setting cannot leave
    /// the app unable to start its own service. The trade is that a typo would silently launch a
    /// different executable, which is why <see cref="ServiceExeResolution.FellBack"/> exists and the
    /// settings window says so out loud.</para>
    /// <para>The setting outranks the environment variable deliberately: the not-found message tells
    /// the user to fix the path in Settings, so a stale ambient variable that silently won would make
    /// that instruction a lie.</para></summary>
    public static ServiceExeResolution Resolve(string? configured)
    {
        List<ServiceExeCandidate> candidates = new(3);
        Add(ServiceExeSource.Setting, configured);
        Add(ServiceExeSource.Environment, Environment.GetEnvironmentVariable(ServiceExeOverrideVariable));
        Add(ServiceExeSource.BesideApp, Path.Combine(AppContext.BaseDirectory, ServiceExeName));
        return new ServiceExeResolution(candidates);

        void Add(ServiceExeSource source, string? path)
        {
            // A blank source is "not configured" and does not appear at all; a blank BesideApp is
            // impossible. Nothing downstream has to special-case empty strings.
            if (string.IsNullOrWhiteSpace(path))
                return;
            string trimmed = path.Trim();
            candidates.Add(new ServiceExeCandidate(source, trimmed, IsUsable(trimmed)));
        }
    }

    /// <summary>Absolute and present. A relative path is rejected rather than resolved: it would bind
    /// to the working directory, which in a real launch is Program Files or System32.</summary>
    private static bool IsUsable(string path)
    {
        try
        {
            return Path.IsPathFullyQualified(path) && File.Exists(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // A hand-typed path can be malformed enough to throw. Unusable is the honest answer, and
            // the caller's message already names the path.
            return false;
        }
    }

    /// <summary>A lowercase sentence fragment, because every caller prefixes it — the status bar with
    /// "Disconnected — " and the dry run with "Dry run failed: ". Names every location checked, since
    /// with nothing usable anywhere there is no single path worth singling out.</summary>
    internal static string NotFoundMessage(ServiceExeResolution resolution)
    {
        List<string> checkedPaths = [];
        foreach (ServiceExeCandidate candidate in resolution.Candidates)
            checkedPaths.Add($"\"{candidate.Path}\"");

        return "service is not running and no service executable could be found (checked " +
               string.Join(", ", checkedPaths) +
               ") — set the service executable path in Settings";
    }

    /// <summary>Only a path the user chose is one they can act on, so only then is the setting worth
    /// naming.</summary>
    private static string SettingsHint(ServiceExeCandidate chosen) =>
        chosen.Source == ServiceExeSource.Setting
            ? " (check the service executable path in Settings)"
            : "";
}
