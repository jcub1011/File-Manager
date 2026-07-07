using FileManager.Contracts.Primitives;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FileManager.Contracts.IPC;

public static class ServiceLauncher
{
    /// <summary>Environment override for the service executable path — F5 development, where
    /// the UI and the Service publish to different directories.</summary>
    public const string ServiceExeOverrideVariable = "FILEMANAGER_SERVICE_EXE";

    private const string ServiceExeName = "FileManager.Service.exe";
    private const int RetryCount = 20;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>Connect; on failure start FileManager.Service.exe and retry with backoff
    /// (250 ms × 20 ≈ 5 s budget, §3.3). Concurrent callers may both spawn a process; the
    /// service's single-instance mutex makes the loser exit immediately, so the race is benign.</summary>
    public static async Task<Result<IpcClient, string>> ConnectOrStartAsync(CancellationToken ct = default)
    {
        Result<IpcClient, string> first = await IpcClient.ConnectAsync(ct).ConfigureAwait(false);
        if (first.IsSuccess)
            return first;

        string exePath = ResolveServiceExePath();
        if (!File.Exists(exePath))
            return $"service is not running and its executable was not found at \"{exePath}\" " +
                   $"(set {ServiceExeOverrideVariable} to override)";

        try
        {
            using Process? process = Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(exePath)!,
            });
            if (process is null)
                return $"failed to start \"{exePath}\"";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return $"failed to start \"{exePath}\": {ex.Message}";
        }

        string lastError = "unknown";
        for (int attempt = 0; attempt < RetryCount; attempt++)
        {
            await Task.Delay(RetryDelay, ct).ConfigureAwait(false);
            Result<IpcClient, string> retry = await IpcClient.ConnectAsync(ct).ConfigureAwait(false);
            if (retry.IsSuccess)
                return retry;
            retry.TryGetError(out lastError!);
        }
        return $"service was started but did not accept a connection within the retry budget: {lastError}";
    }

    private static string ResolveServiceExePath()
    {
        string? overridden = Environment.GetEnvironmentVariable(ServiceExeOverrideVariable);
        if (!string.IsNullOrWhiteSpace(overridden))
            return overridden;
        return Path.Combine(AppContext.BaseDirectory, ServiceExeName);
    }
}
