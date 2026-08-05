using FileManager.Core;
using FileManager.Core.Audit;
using FileManager.Core.Disposition;
using FileManager.Core.DryRun;
using FileManager.Core.Files;
using FileManager.Core.Filtering;
using FileManager.Core.IPC;
using FileManager.Core.IPC.Handlers;
using FileManager.Core.Jobs;
using FileManager.Core.Journal;
using FileManager.Core.Locking;
using FileManager.Core.Observability;
using FileManager.Core.Placement;
using FileManager.Core.Platform;
using FileManager.Core.Preflight;
using FileManager.Core.Profiles;
using FileManager.Core.Runs;
using FileManager.Core.Runs.Reconcile;
using FileManager.Core.Scanning;
using FileManager.Core.Settings;
using FileManager.Core.Watching;
using FileManager.Platform.Windows;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;

namespace FileManager.Service;

/// <summary>The service's object graph, lifted out of <see cref="Program"/> so a test can build it.
/// It previously lived inside <c>Program.Main</c> in an assembly with no <c>InternalsVisibleTo</c>,
/// which meant no test could construct the container: a missing registration or a handler absent from
/// the dispatch table failed only at runtime, in production. The end-to-end smoke test worked around
/// that by hand-recomposing the graph — and its copy had already drifted three handlers behind,
/// including the streaming dry-run handler the UI uses for every dry run.
/// <para>Registration ORDER is preserved from the original and is behaviour: MSDI resolves
/// last-registration-wins for a duplicated service type.</para>
/// <para>The composition root is the one place that binds Windows implementations to Core's platform
/// interfaces; a Linux host would be a sibling composition root, not a branch here (§10.5).</para></summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal static class EngineComposition
{
    /// <summary>Every handler type the service dispatches, in wire-vocabulary order. Named explicitly
    /// (no assembly scanning) to stay inside the §1 AOT constraints; the dispatch table is then derived
    /// from these registrations rather than from a second hand-written list of the same 17 names.</summary>
    public static void AddEngine(IServiceCollection services, EnginePaths paths)
    {
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
        // The source-phase → survivor-set → destination-sweep sequencing, shared by the streamed
        // dry-run handler and the live run pipeline. Sharing it is what keeps a preview and a real
        // run from ever describing different work — which under SyncMode.Mirror is the difference
        // between a previewed deletion and an unpreviewed one.
        services.AddSingleton<IProfilePlanner, ProfilePlanner>();
        services.AddSingleton<IIpcEndpointProvider, WindowsIpcEndpointProvider>();
        services.AddSingleton<IAutostartRegistrar, WindowsAutostartRegistrar>();

        // Engine settings (§9). Defaults only for now; reconciling with settings.json is a later concern.
        services.AddSingleton(new EngineConfig());

        // Written by EngineHost during startup, read by GetStatusHandler on every poll — how a
        // degraded startup reaches a client that connected too late to see the event.
        services.AddSingleton<EngineStartupState>();

        // Durable safety substrate (§4.3, §4.6, §4.7) — the write-ahead journal, in-memory
        // registries, atomic placement, rollback, disposition, and crash recovery. Not yet driven
        // by a live executor; recovery runs at startup (I-RECOVER-FIRST) and the machinery is
        // exercised by tests.
        services.AddSingleton<PathLockRegistry>();
        services.AddSingleton<SelfWriteSuppressionRegistry>();
        services.AddSingleton<SourcePriorityRegistry>();
        services.AddSingleton<IJobJournal, JobJournal>();
        services.AddSingleton<IDispositionAuditLog, DispositionAuditLog>();
        // A SIBLING trail to the disposition one, not a widened record: a Mirror orphan is a
        // destination file removed because no source writes to it, which is not an OnSuccess
        // disposition of a source and must never read like one in the no-loss audit trail.
        services.AddSingleton<IReconcileAuditLog, MirrorDeletionAuditLog>();
        services.AddSingleton<ITransientRetryPolicy, TransientRetryPolicy>();
        services.AddSingleton<IVolumeInfoProvider, WindowsVolumeInfoProvider>();
        services.AddSingleton<IMetadataPreserver, WindowsMetadataPreserver>();
        services.AddSingleton<ITrashService, WindowsTrashService>();
        services.AddSingleton<IPathCanonicalizer, WindowsPathCanonicalizer>();
        services.AddSingleton<IDiskPreflight, DiskPreflight>();
        services.AddSingleton<IAtomicPlacer, AtomicPlacer>();
        services.AddSingleton<IRollbackExecutor, RollbackExecutor>();
        services.AddSingleton<ISourceDispositionService, SourceDispositionService>();
        services.AddSingleton<ICrashRecovery, CrashRecovery>();

        // Live single-job vertical (Set 3): the trigger queue, pause state, event bus, per-job log
        // store, profile matcher, plan factory, executor, and orchestrator that drive the substrate.
        services.AddSingleton<IPauseStateService, PauseStateService>();
        services.AddSingleton<IEngineEventBus, EngineEventBus>();
        // Process-wide by necessity: it debounces across every operation and gates on a
        // whole-process GC reading, so a per-request instance could not do either.
        services.AddSingleton<MemoryTrimCoordinator>();
        services.AddSingleton<IMemoryTrimCoordinator>(sp => sp.GetRequiredService<MemoryTrimCoordinator>());
        services.AddSingleton<IJobLogStore, JobLogStore>();
        services.AddSingleton<ITriggerQueue, TriggerQueue>();
        services.AddSingleton<IProfileMatcher, ProfileMatcher>();
        services.AddSingleton<JobPlanFactory>();
        services.AddSingleton<IJobExecutor, JobExecutor>();

        // Snapshot-driven runs (plan → approve → execute) and the Mirror deletion phase. Registered
        // BEFORE the orchestrator because the orchestrator settles every payload against its run — a
        // dependency that only goes one way, so there is no cycle: the coordinator never knows about
        // the orchestrator.
        services.AddSingleton<IMirrorDeletionPass, MirrorDeletionPass>();
        // A seam, not a feature: this reproduces today's §3.4 priority rule exactly. It exists so a
        // future speed/byte-balancing strategy for contested destination paths is a planning-only
        // change — see ISourceSelector's doc for the recorded intent and its platform prerequisite.
        services.AddSingleton<ISourceSelector, PrioritySourceSelector>();
        // Concrete + interface, the MemoryTrimCoordinator pattern: RunProfileHandler and
        // JobOrchestrator both write to run state, so both must resolve THE SAME instance — and
        // EngineHost resolves the concrete type to drain it at shutdown.
        services.AddSingleton<RunCoordinator>();
        services.AddSingleton<IRunCoordinator>(sp => sp.GetRequiredService<RunCoordinator>());
        services.AddSingleton<IRunSettleSink>(sp => sp.GetRequiredService<RunCoordinator>());

        services.AddSingleton<IJobOrchestrator, JobOrchestrator>();

        // Explicit dispatch table — no reflection-based handler discovery (§1 AOT constraints).
        // Registered against IIpcRequestHandler so the table below is DERIVED from these lines. The
        // previous shape listed all 17 types again inside the table factory, and the failure mode of a
        // mismatch was silent: register the singleton, forget the array entry, and the service builds,
        // starts, and answers NOT_IMPLEMENTED to the UI at runtime.
        services.AddSingleton<IIpcRequestHandler, GetStatusHandler>();
        services.AddSingleton<IIpcRequestHandler, GetMatchingProfilesHandler>();
        services.AddSingleton<IIpcRequestHandler, RunProfileHandler>();
        services.AddSingleton<IIpcRequestHandler, ApproveRunHandler>();
        services.AddSingleton<IIpcRequestHandler, CancelRunHandler>();
        // Streaming handler: IpcServer type-tests the table's values for IIpcStreamingRequestHandler.
        services.AddSingleton<IIpcRequestHandler, GetRunPlanStreamHandler>();
        services.AddSingleton<IIpcRequestHandler, SetPausedHandler>();
        services.AddSingleton<IIpcRequestHandler, GetRecentJobsHandler>();
        services.AddSingleton<IIpcRequestHandler, GetJobLogHandler>();
        services.AddSingleton<IIpcRequestHandler, ListProfilesHandler>();
        services.AddSingleton<IIpcRequestHandler, GetProfileHandler>();
        services.AddSingleton<IIpcRequestHandler, SaveProfileHandler>();
        services.AddSingleton<IIpcRequestHandler, DeleteProfileHandler>();
        services.AddSingleton<IIpcRequestHandler, ValidateProfileHandler>();
        services.AddSingleton<IIpcRequestHandler, DryRunHandler>();
        // Streaming handler: IpcServer type-tests the table's values for IIpcStreamingRequestHandler,
        // which derives from IIpcRequestHandler, so this registration still routes it correctly.
        services.AddSingleton<IIpcRequestHandler, DryRunStreamHandler>();
        services.AddSingleton<IIpcRequestHandler, GetSettingsHandler>();
        services.AddSingleton<IIpcRequestHandler, UpdateSettingsHandler>();
        services.AddSingleton<IIpcRequestHandler, RelocateProfilesHandler>();
        services.AddSingleton<IIpcRequestHandler, ShutdownHandler>();
        services.AddSingleton<IReadOnlyDictionary<string, IIpcRequestHandler>>(provider =>
        {
            Dictionary<string, IIpcRequestHandler> table = new(StringComparer.Ordinal);
            foreach (IIpcRequestHandler handler in provider.GetServices<IIpcRequestHandler>())
                table.Add(handler.RequestType, handler);   // Add, not [], so a duplicate throws at build
            return table;
        });
        services.AddSingleton<IIpcServer, IpcServer>();

        services.AddHostedService<EngineHost>();
    }
}
