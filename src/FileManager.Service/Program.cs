using FileManager.Core;
using FileManager.Core.Audit;
using FileManager.Core.Disposition;
using FileManager.Core.DryRun;
using FileManager.Core.Files;
using FileManager.Core.Filtering;
using FileManager.Core.IPC;
using FileManager.Core.IPC.Handlers;
using FileManager.Core.Journal;
using FileManager.Core.Locking;
using FileManager.Core.Placement;
using FileManager.Core.Platform;
using FileManager.Core.Preflight;
using FileManager.Core.Profiles;
using FileManager.Core.Settings;
using FileManager.Core.Scanning;
using FileManager.Core.Watching;
using FileManager.Platform.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;

namespace FileManager.Service;

// The composition root is the one place that binds Windows implementations to Core's platform
// interfaces; a Linux host would be a sibling composition root, not a branch here (§10.5).
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal static class Program
{
    public static void Main(string[] args)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

        // File logging (spec §9). Configured in code — no reflection-based appsettings binding
        // (§1 AOT constraints). Rolling daily, 14-day retention: logs\service-YYYYMMDD.log.
        EnginePaths paths = EnginePaths.Default();
        Directory.CreateDirectory(paths.LogsDirectory);
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                Path.Combine(paths.LogsDirectory, "service-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14)
            .CreateLogger();

        // Last-resort safety net: capture crashes that escape the host so they reach the log
        // before the process dies (mirrors the UI's handlers in FileManager.UI/Program.cs).
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Fatal(e.ExceptionObject as Exception, "Unhandled exception in service host (terminating: {IsTerminating})", e.IsTerminating);
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error(e.Exception, "Unobserved task exception in service host");
            e.SetObserved();
        };

        IServiceCollection services = builder.Services;
        builder.Logging.ClearProviders();
        services.AddSerilog();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(paths);
        services.AddSingleton<IFileSystemService, FileSystemService>();
        services.AddSingleton<IFilterCompiler, FilterCompiler>();
        services.AddSingleton<IProfileValidator, ProfileValidator>();
        services.AddSingleton<IProfileStore, ProfileStore>();
        services.AddSingleton<IProfileCatalog, ProfileCatalog>();
        services.AddSingleton<ISettingsProvider, SettingsService>();
        // The process-wide scan scheduler owns all directory-enumeration worker threads (global +
        // per-drive budgets), shared by the source scan and the destination sweep.
        services.AddSingleton<IScanScheduler, ScanScheduler>();
        services.AddSingleton<ISourceScanner, SourceScanner>();
        services.AddSingleton<IFileHasher, FileHasher>();
        services.AddSingleton<IConflictResolver, ConflictResolver>();
        services.AddSingleton<DestinationProjector>();
        services.AddSingleton<IDryRunEngine, DryRunEngine>();
        services.AddSingleton<IIpcEndpointProvider, WindowsIpcEndpointProvider>();
        services.AddSingleton<IAutostartRegistrar, WindowsAutostartRegistrar>();

        // Engine settings (§9). Defaults only for now; reconciling with settings.json is a later concern.
        services.AddSingleton(new EngineConfig());

        // Durable safety substrate (§4.3, §4.6, §4.7) — the write-ahead journal, in-memory
        // registries, atomic placement, rollback, disposition, and crash recovery. Not yet driven
        // by a live executor; recovery runs at startup (I-RECOVER-FIRST) and the machinery is
        // exercised by tests.
        services.AddSingleton<PathLockRegistry>();
        services.AddSingleton<SelfWriteSuppressionRegistry>();
        services.AddSingleton<SourcePriorityRegistry>();
        services.AddSingleton<IJobJournal, JobJournal>();
        services.AddSingleton<IDispositionAuditLog, DispositionAuditLog>();
        services.AddSingleton<ITransientRetryPolicy, TransientRetryPolicy>();
        services.AddSingleton<IVolumeInfoProvider, WindowsVolumeInfoProvider>();
        services.AddSingleton<IMetadataPreserver, WindowsMetadataPreserver>();
        services.AddSingleton<ITrashService, WindowsTrashService>();
        services.AddSingleton<IDiskPreflight, DiskPreflight>();
        services.AddSingleton<IAtomicPlacer, AtomicPlacer>();
        services.AddSingleton<IRollbackExecutor, RollbackExecutor>();
        services.AddSingleton<ISourceDispositionService, SourceDispositionService>();
        services.AddSingleton<ICrashRecovery, CrashRecovery>();

        // Explicit dispatch table — no reflection-based handler discovery (§1 AOT constraints).
        services.AddSingleton<GetStatusHandler>();
        services.AddSingleton<ListProfilesHandler>();
        services.AddSingleton<GetProfileHandler>();
        services.AddSingleton<SaveProfileHandler>();
        services.AddSingleton<DeleteProfileHandler>();
        services.AddSingleton<ValidateProfileHandler>();
        services.AddSingleton<DryRunHandler>();
        services.AddSingleton<DryRunStreamHandler>();
        services.AddSingleton<GetSettingsHandler>();
        services.AddSingleton<UpdateSettingsHandler>();
        services.AddSingleton<ShutdownHandler>();
        services.AddSingleton<IReadOnlyDictionary<string, IIpcRequestHandler>>(provider =>
        {
            IIpcRequestHandler[] handlers =
            [
                provider.GetRequiredService<GetStatusHandler>(),
                provider.GetRequiredService<ListProfilesHandler>(),
                provider.GetRequiredService<GetProfileHandler>(),
                provider.GetRequiredService<SaveProfileHandler>(),
                provider.GetRequiredService<DeleteProfileHandler>(),
                provider.GetRequiredService<ValidateProfileHandler>(),
                provider.GetRequiredService<DryRunHandler>(),
                provider.GetRequiredService<DryRunStreamHandler>(),
                provider.GetRequiredService<GetSettingsHandler>(),
                provider.GetRequiredService<UpdateSettingsHandler>(),
                provider.GetRequiredService<ShutdownHandler>(),
            ];
            Dictionary<string, IIpcRequestHandler> table = new(StringComparer.Ordinal);
            foreach (IIpcRequestHandler handler in handlers)
                table.Add(handler.RequestType, handler);
            return table;
        });
        services.AddSingleton<IIpcServer, IpcServer>();

        services.AddHostedService<EngineHost>();

        try
        {
            builder.Build().Run();
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }
}
