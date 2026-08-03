using Serilog;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime;

namespace FileManager.UI.Services;

/// <summary>Memory self-instrumentation for the UI process, mirroring what the service already does
/// (<c>EngineHost.LogGcConfiguration</c> and <c>DryRunStreamHandler</c>'s per-run memory line).
///
/// <para>Why it exists: every number in <c>docs/dry-run-memory-optimization.md</c> is a
/// <em>retained-heap delta measured in a JIT test host</em> — the right gauge for comparing
/// optimization passes against each other, and the wrong one for "is the shipped app comfortable on a
/// constrained machine". What a user reports is the private commit of the NativeAOT
/// <c>FileManager.UI.exe</c>: the managed heap <em>plus</em> the AOT runtime, Avalonia, the render
/// surface, font atlases and the loaded profile state. Nothing measured that, and the UI has no GC
/// configuration of its own, so nobody knew whether it comes back down after a preview is cleared
/// either. These log lines answer both without a human reading Task Manager.</para></summary>
internal static class UiMemoryLog
{
    /// <summary>Logs the GC's resolved configuration once at startup. The UI deliberately sets no GC
    /// properties (unlike the service, which runs with <c>ConcurrentGarbageCollection=false</c> —
    /// blocking compacting gen2s are the wrong trade for a process with a UI thread), so this line is
    /// how you confirm that rather than assume it: the variables dictionary reports only knobs that
    /// were explicitly set, and the two flags read back what the runtime actually resolved.</summary>
    public static void LogGcConfiguration()
    {
        try
        {
            if (!Log.IsEnabled(Serilog.Events.LogEventLevel.Information))
                return;
            List<string> entries = [];
            foreach (KeyValuePair<string, object> variable in GC.GetConfigurationVariables())
                entries.Add($"{variable.Key}={variable.Value}");
            entries.Sort(StringComparer.Ordinal);
            // LatencyMode is the readable proxy for background GC: with concurrent GC off it reports
            // Batch, with it on (the default) Interactive. There is no direct IsConcurrentGC property.
            Log.Information(
                "UI GC configuration: server={Server}, latency={Latency}, explicitly-set variables: {Variables}",
                GCSettings.IsServerGC, GCSettings.LatencyMode,
                entries.Count == 0 ? "(none)" : string.Join(", ", entries));
        }
        catch (Exception ex)
        {
            // Last resort: a diagnostic must never be able to stop the app from starting.
            Log.Warning(ex, "Could not read the UI's GC configuration");
        }
    }

    /// <summary>Samples the four memory figures at a named point in the app's life, optionally with the
    /// allocation churn since <paramref name="allocatedBefore"/> (pass
    /// <see cref="AllocatedSnapshot"/> taken earlier; omit for a plain snapshot).
    ///
    /// <para>The four are NOT interchangeable — logging all of them is the whole point, because they
    /// answer different questions:</para>
    /// <list type="bullet">
    /// <item><c>managed</c> — the managed heap as the GC last accounted it.</item>
    /// <item><c>heap</c> — live+garbage bytes the GC currently tracks.</item>
    /// <item><c>committed</c> — what the GC has committed from the OS.</item>
    /// <item><c>private</c> — the process's private commit, i.e. what Task Manager shows a user.</item>
    /// </list>
    /// <para><c>committed</c> ≫ <c>heap</c> means the residual is committed-but-free GC heap (a
    /// GC-configuration or trim problem, not a lifetime one); <c>heap</c> ≫ the idle sample means
    /// something is still retained (a lifetime problem). <c>allocated</c> is churn, nearly all of it
    /// already dead — the number allocation-avoidance work moves, and the only visibility we have into
    /// the transient burst a large preview costs.</para>
    ///
    /// <para><strong>Never forces a collection.</strong> <c>GC.GetTotalMemory(true)</c> would perturb
    /// the very number being measured and add a blocking gen2 to a UI thread. That also means a
    /// snapshot taken right after a big allocation burst reads near-peak, not settled — compare the
    /// <c>preview-cleared</c> line against <c>idle</c> for the residual, not the
    /// <c>preview-applied</c> one.</para></summary>
    public static void Sample(string stage, long? allocatedBefore = null)
    {
        try
        {
            if (!Log.IsEnabled(Serilog.Events.LogEventLevel.Information))
                return;
            GCMemoryInfo gcInfo = GC.GetGCMemoryInfo();
            long managedBytes = GC.GetTotalMemory(false);
            long privateBytes;
            using (Process self = Process.GetCurrentProcess())
                privateBytes = self.PrivateMemorySize64;
            if (allocatedBefore is { } before)
            {
                Log.Information(
                    "UI memory at {Stage}: managed {ManagedMb}MB, GC heap {HeapMb}MB, GC committed {CommittedMb}MB, " +
                    "process private {PrivateMb}MB, gen0/1/2 {Gen0}/{Gen1}/{Gen2}, allocated since run start {AllocatedMb}MB",
                    stage, managedBytes >> 20, gcInfo.HeapSizeBytes >> 20, gcInfo.TotalCommittedBytes >> 20,
                    privateBytes >> 20, GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2),
                    (GC.GetTotalAllocatedBytes() - before) >> 20);
            }
            else
            {
                Log.Information(
                    "UI memory at {Stage}: managed {ManagedMb}MB, GC heap {HeapMb}MB, GC committed {CommittedMb}MB, " +
                    "process private {PrivateMb}MB, gen0/1/2 {Gen0}/{Gen1}/{Gen2}",
                    stage, managedBytes >> 20, gcInfo.HeapSizeBytes >> 20, gcInfo.TotalCommittedBytes >> 20,
                    privateBytes >> 20, GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not sample UI memory at {Stage}", stage);
        }
    }

    /// <summary>The allocation counter to hand back to <see cref="Sample"/> as
    /// <c>allocatedBefore</c>. Cheap (a read of per-thread counters), so it is taken unconditionally
    /// rather than behind a level check.</summary>
    public static long AllocatedSnapshot() => GC.GetTotalAllocatedBytes();
}
