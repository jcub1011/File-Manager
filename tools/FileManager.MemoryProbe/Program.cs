using FileManager.Contracts.DryRun;
using FileManager.Contracts.IPC;
using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace FileManager.MemoryProbe;

/// <summary>Measures the PUBLISHED FileManager.Service across one streamed dry run.
///
/// <para>Why an external harness rather than a test: everything about GC configuration is invisible
/// in-process. xUnit runs under JIT with the test host's runtimeconfig.json, while the service is
/// NativeAOT with its knobs embedded at ILC time — so a test can prove the decision logic (as
/// <c>MemoryTrimCoordinatorTests</c> does) but never that the shipped binary's footprint moved.</para>
///
/// <para>It drives <c>IpcClient.DryRunStreamAsync</c>, the same path the UI uses. Note the earlier
/// manual procedure in ADR 0001 used <c>RequestAsync&lt;DryRunResponse&gt;</c>, which would trip
/// IPC_RESPONSE_TOO_LARGE at this scale — a single frame cannot carry 357k entries.</para></summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Options.Result parsed = Options.Parse(args);
        if (parsed.Error is { } error)
        {
            Console.Error.WriteLine($"error: {error}");
            Console.Error.WriteLine("run with --help for usage");
            return 2;
        }
        if (parsed.Options is not { } options)
        {
            foreach (string line in Options.HelpLines)
                Console.WriteLine(line);
            return 0;
        }

        // Checked BEFORE the trees, which take minutes to build: a mistyped or unpublished service
        // path should cost a second, not a tree generation followed by a bare Win32Exception from
        // Process.Start. The publish-directory hint is the common cause — `dotnet publish` leaves the
        // exe under bin/<config>/<tfm>/<rid>/publish, and its native link silently no-ops without the
        // MSVC toolchain, leaving that directory empty.
        if (!options.GenerateOnly && ValidateServiceExe(options.ServiceExePath!) is { } serviceError)
        {
            Console.Error.WriteLine($"error: {serviceError}");
            return 2;
        }

        try
        {
            TreeBuilder.EnsureTrees(options);
            if (options.GenerateOnly)
                return 0;
            return await MeasureAsync(options);
        }
        catch (Exception ex)
        {
            // Last resort: a harness that dies with a bare stack trace and no context wastes the
            // several minutes of tree generation that preceded it.
            Console.Error.WriteLine($"probe failed: {ex}");
            return 3;
        }
    }

    /// <summary>Null when the path is usable, else a message that names the likely cause. Returning a
    /// message rather than throwing keeps the "bad input" exit code (2) distinct from "the run
    /// failed" (3+), which is what makes this usable in a script.</summary>
    private static string? ValidateServiceExe(string path)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return $"--service is not a usable path: {ex.Message}";
        }

        if (File.Exists(full))
            return null;

        string directory = Path.GetDirectoryName(full) ?? "";
        if (directory.EndsWith(Path.DirectorySeparatorChar + "publish", StringComparison.OrdinalIgnoreCase)
            && Directory.Exists(directory)
            && Directory.GetFileSystemEntries(directory).Length == 0)
        {
            return $"the publish directory {directory} exists but is EMPTY — `dotnet publish` did not " +
                "complete. A NativeAOT publish needs the MSVC/C++ toolchain: run it from a Visual " +
                "Studio Developer PowerShell, or put vswhere.exe on PATH " +
                "(C:\\Program Files (x86)\\Microsoft Visual Studio\\Installer). Without it the ILC " +
                "native link fails and leaves this directory empty.";
        }

        return $"the service executable {full} does not exist. Publish it first:\n" +
            "  dotnet publish src/FileManager.Service -c Release -r win-x64";
    }

    private static async Task<int> MeasureAsync(Options options)
    {
        // A pipe name unique to this run, so the probe can never accidentally attach to (or be
        // attached to by) a developer's real service and measure the wrong process.
        string pipeName = "fm-memprobe-" + Guid.NewGuid().ToString("N")[..12];
        string serviceRoot = Path.Combine(Path.GetTempPath(), "fm-memprobe-state-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(serviceRoot);

        // The client resolves its endpoint from THIS process's environment, so both halves have to be
        // set: here for IpcClient, and on the child's ProcessStartInfo for the server.
        Environment.SetEnvironmentVariable(IpcEndpoint.PipeNameOverrideVariable, pipeName);

        Console.WriteLine($"Launching {options.ServiceExePath}");
        Console.WriteLine($"  pipe   {pipeName}");
        Console.WriteLine($"  state  {serviceRoot}");

        using Process service = StartService(options.ServiceExePath!, pipeName, serviceRoot);
        try
        {
            using MemorySampler sampler = new(service);
            List<PhaseReading> readings = [];

            // Let startup settle before calling anything an idle baseline: the host is still creating
            // directories, running crash recovery and opening the pipe for the first second or so.
            await Task.Delay(TimeSpan.FromSeconds(3));
            if (service.HasExited)
            {
                Console.Error.WriteLine(
                    $"the service exited immediately (code {service.ExitCode}). The single-instance mutex is " +
                    "per-user regardless of pipe name — stop any running FileManager.Service and retry.");
                return 4;
            }

            Result<IpcClient, string> connected = await ConnectWithRetryAsync(pipeName);
            if (connected.TryGetError(out string? connectError))
            {
                Console.Error.WriteLine($"could not connect to the service: {connectError}");
                return 4;
            }
            connected.TryGetValue(out IpcClient? client);

            await using (client)
            {
                MemorySample idle = sampler.Sample();

                Stopwatch watch = Stopwatch.StartNew();
                DryRunOutcome outcome = await RunDryRunAsync(client!, options);
                watch.Stop();

                if (outcome.Error is { } runError)
                {
                    Console.Error.WriteLine($"dry run failed: {runError}");
                    return 5;
                }

                // Sample the instant the run returns, so the peak covers the tail of the run rather
                // than only whatever the 100 ms poll happened to catch.
                MemorySample settledNow = sampler.Sample();

                Console.WriteLine();
                Console.WriteLine(
                    $"dry run: {outcome.SourceFiles:N0} source files, {outcome.DestinationFiles:N0} destination " +
                    $"files, truncated={outcome.Truncated}, {watch.ElapsedMilliseconds:N0}ms");
                Console.WriteLine($"phase transitions: {string.Join(" -> ", outcome.Phases)}");

                // The trim coordinator debounces on a 25s quiet period, so a t+0 reading shows none of
                // its effect. Without this row the trim looks broken rather than merely deferred.
                Console.WriteLine($"waiting {options.SettleSeconds}s for the post-run trim to fire …");
                await Task.Delay(TimeSpan.FromSeconds(options.SettleSeconds));
                MemorySample settledLater = sampler.Sample();

                // Read the peak LAST. It is a high-water mark over everything observed, so reading it
                // before the settle samples could report a "peak" lower than a later row — which reads
                // as a broken measurement rather than what it is (a stale snapshot).
                (long peakPrivate, long peakWorkingSet) = sampler.Peak;
                string settleLabel = $"settle t+{options.SettleSeconds}s";

                Console.WriteLine();
                readings.Add(new PhaseReading("idle-before", idle.PrivateBytes, idle.WorkingSetBytes));
                readings.Add(new PhaseReading("peak", peakPrivate, peakWorkingSet));
                readings.Add(new PhaseReading("settle t+0s", settledNow.PrivateBytes, settledNow.WorkingSetBytes));
                readings.Add(new PhaseReading(settleLabel, settledLater.PrivateBytes, settledLater.WorkingSetBytes));
                foreach (PhaseReading reading in readings)
                    Report(reading);

                WriteCsv(options, readings);
                return Verdict(options, settledLater);
            }
        }
        finally
        {
            StopService(service);
            TryDelete(serviceRoot);
        }
    }

    /// <summary>Runs the dry run and DROPS the report before returning.
    ///
    /// <para>The harness receives the client-side reassembled report — at 357k entries that is
    /// hundreds of MB in THIS process. Building and releasing it inside a non-inlined frame keeps the
    /// harness from OOMing, and keeps anyone reading the output from confusing the two processes'
    /// numbers: every figure this tool prints is the SERVICE's.</para></summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<DryRunOutcome> RunDryRunAsync(IpcClient client, Options options)
    {
        List<string> phases = [];
        Progress<DryRunProgress> progress = new(p =>
        {
            string name = p.Phase.ToString();
            lock (phases)
            {
                if (phases.Count == 0 || phases[^1] != name)
                    phases.Add(name);
            }
        });

        Profile profile = BuildProfile(options);
        Result<DryRunReport, IpcError> result = await client.DryRunStreamAsync(
            new DryRunStreamRequest { ProfileId = profile.Id, InlineProfile = profile }, progress);

        if (result.TryGetError(out IpcError? error))
            return new DryRunOutcome(0, 0, false, phases, $"{error.Code}: {error.Message}");
        result.TryGetValue(out DryRunReport? report);

        DryRunOutcome outcome = new(
            report!.SourceFiles.Count, report.DestinationFiles.Count, report.Truncated, phases, null);
        // report goes out of scope here (this frame is deliberately not inlined into the caller's),
        // so the harness's own footprint never enters the readings — every figure this tool prints is
        // sampled from the SERVICE process, not this one.
        return outcome;
    }

    private sealed record DryRunOutcome(
        int SourceFiles, int DestinationFiles, bool Truncated, List<string> Phases, string? Error);

    private static Profile BuildProfile(Options options) => new()
    {
        SchemaVersion = 2,
        Id = Guid.NewGuid(),
        Name = "memprobe",
        Active = true,
        // Mirror with an empty survivor set classifies EVERY destination file as a Deleted orphan —
        // the maximum-output sweep, and the shape the memory work targets.
        SyncMode = options.Mirror ? SyncMode.Mirror : SyncMode.AdditiveArchive,
        ScanDestination = true,
        TargetLayout = TargetLayout.PreserveStructure,
        Triggers = new TriggerSettings { ManualShell = true, Watcher = false, Schedule = null },
        Sources = [new SourceConfig { Path = options.SourceRoot }],
        Targets = [new TargetConfig { Path = options.DestinationRoot }],
        Policies = new PolicySettings
        {
            ConflictResolution = ConflictResolution.Skip,
            OverwriteHandling = OverwriteHandling.StageOverwrites,
            // The app default. Free here regardless: every generated file is 0 bytes, so hashing
            // cannot dominate the wall time and skew what is being measured. (SizeTimestamp would be
            // cheaper still but is reserved and fails validation.)
            VerificationMethod = VerificationMethod.XxHash128,
            OnSuccess = OnSuccessAction.KeepSource,
            ArchiveFolder = null,
            OnFailure = OnFailureAction.AbortRestoreAndClean,
            MetadataOnConflict = MetadataOnConflict.WarnAndContinue,
        },
        Filters = null,
        Logging = new LoggingSettings { Verbosity = LogVerbosity.FailuresAndSkips, NotifyOnFailure = true },
    };

    private static Process StartService(string exePath, string pipeName, string serviceRoot)
    {
        ProcessStartInfo info = new(Path.GetFullPath(exePath))
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(exePath))!,
        };
        info.Environment[IpcEndpoint.PipeNameOverrideVariable] = pipeName;
        // Redirect the whole engine root away from the developer's real state: EnginePaths anchors on
        // LOCALAPPDATA, so profiles, logs, the journal and the dry-run scratch all move with it.
        info.Environment["LOCALAPPDATA"] = serviceRoot;
        return Process.Start(info) ?? throw new InvalidOperationException("could not start the service process");
    }

    private static async Task<Result<IpcClient, string>> ConnectWithRetryAsync(string pipeName)
    {
        // The service opens its pipe a moment after launch (crash recovery runs first, by design), so
        // the first connect can legitimately fail.
        string last = "no attempt made";
        for (int attempt = 0; attempt < 20; attempt++)
        {
            Result<IpcClient, string> result = await IpcClient.ConnectAsync();
            if (result.TryGetValue(out IpcClient? client))
                return client;
            result.TryGetError(out string? error);
            last = error ?? "unknown";
            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }
        return $"gave up after 5s on pipe \"{pipeName}\": {last}";
    }

    private static void StopService(Process service)
    {
        try
        {
            if (service.HasExited)
                return;
            service.Kill(entireProcessTree: true);
            service.WaitForExit(TimeSpan.FromSeconds(10));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"could not stop the service process: {ex.Message}");
        }
    }

    private static void TryDelete(string directory)
    {
        try { Directory.Delete(directory, recursive: true); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"could not clean up {directory}: {ex.Message}");
        }
    }

    private static void Report(PhaseReading reading) =>
        Console.WriteLine(
            $"  {reading.Phase,-16} private {reading.PrivateBytes >> 20,6:N0} MB   " +
            $"working set {reading.WorkingSetBytes >> 20,6:N0} MB");

    private static void WriteCsv(Options options, List<PhaseReading> readings)
    {
        if (options.CsvPath is not { } path)
            return;
        try
        {
            bool exists = File.Exists(path);
            using StreamWriter writer = new(path, append: true);
            if (!exists)
                writer.WriteLine(string.Join(",", PhaseReading.CsvHeader));
            string timestamp = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            foreach (PhaseReading reading in readings)
                writer.WriteLine(reading.ToCsvRow(timestamp, options.Label));
            Console.WriteLine($"appended {readings.Count} rows to {path}");
        }
        catch (Exception ex)
        {
            // The measurement already succeeded and is on stdout; losing the CSV must not lose it.
            Console.Error.WriteLine($"could not write the CSV: {ex.Message}");
        }
    }

    private static int Verdict(Options options, MemorySample settled)
    {
        if (options.BudgetPrivateMb is not { } budget)
            return 0;
        long settledMb = settled.PrivateBytes >> 20;
        if (settledMb <= budget)
        {
            Console.WriteLine($"within budget: {settledMb} MB <= {budget} MB");
            return 0;
        }
        Console.Error.WriteLine($"OVER BUDGET: settled private {settledMb} MB > {budget} MB");
        return 1;
    }
}
