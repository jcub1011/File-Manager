using Serilog;
using System;
using System.Diagnostics;
using System.Runtime;

namespace FileManager.UI.Services;

/// <summary>Returns the memory a closed dry-run preview was holding, at the one moment the app can
/// afford to: preview close.
///
/// <para><strong>Why this exists.</strong> Measured on the published AOT exe (2026-08-03, real profile,
/// 33k source files / 331k destination operations — see
/// <c>docs/dry-run-ui-memory-next-steps.md</c>): idle private commit 108 MB; with a preview loaded
/// 300 MB; <em>ten seconds after clearing the preview</em> still 214 MB, with the gen2 counter
/// unmoved. That last part is the whole problem — an idle UI allocates nothing, so no gen2 ever runs,
/// so nothing is returned until the <em>next</em> run's burst forces one. Which is why a second
/// consecutive run started from a higher floor and peaked 44 MB above the first (private 300 →
/// 354 MB). The service hit exactly this shape and fixed it; its log for those same runs reads
/// <c>GC committed 91MB -&gt; 2MB, heap 58MB -&gt; 2MB</c> in 14 ms.</para>
///
/// <para><strong>Why an explicit collect is acceptable here specifically.</strong>
/// <c>docs/dry-run-service-memory.md</c> §13 records the standing preference against manually
/// controlling GC — <em>"avoid generating the garbage in the first place via pooling and other memory
/// management strategies"</em> — and that still governs the run itself, where the 600 MB burst wants
/// allocation avoidance, not collection. This is the one carve-out that preference allows: preview
/// close is a transition the user already initiated and already expects to be one, nothing is being
/// rendered or scrolled, and there is no run in flight. It is not a mid-run collect.</para>
///
/// <para><strong>Not the service's coordinator.</strong> <c>MemoryTrimCoordinator</c> lives in
/// <c>FileManager.Core</c>, which the UI deliberately does not reference (Contracts only), so this is a
/// deliberate re-implementation of its trim step rather than reuse. It also needs none of that type's
/// machinery: the service debounces because many small operations trigger it, whereas the UI has one
/// discrete trigger per preview.</para></summary>
internal static class UiMemoryTrim
{
    /// <summary>Collects and decommits after a preview was released. Logs the before/after so the
    /// effect is visible rather than assumed — and so the first question this answers is diagnostic:
    /// whether the post-clear heap was <em>garbage</em> (committed drops) or still <em>live</em> (it
    /// does not, and there is a lifetime bug to find instead).
    ///
    /// <para>Synchronous and blocking, on purpose and on the UI thread. Compacting gen2s are visible
    /// jank in general — that is why the service's <c>ConcurrentGarbageCollection=false</c> knob is
    /// explicitly NOT copied into this app's csproj — but a bounded one-off at a transition is a
    /// different thing from making every gen2 blocking for the process's whole life. The elapsed
    /// milliseconds are logged so this claim stays checkable; the service's equivalent runs in
    /// ~14 ms.</para></summary>
    public static void AfterPreviewClosed()
    {
        try
        {
            GCMemoryInfo before = GC.GetGCMemoryInfo();
            long committedBefore = before.TotalCommittedBytes;
            long heapBefore = before.HeapSizeBytes;
            Stopwatch watch = Stopwatch.StartNew();

            // TWO passes, deliberately, matching MemoryTrimCoordinator: with LOH allocations a single
            // aggressive collect is reported not to decommit (dotnet/runtime#78679); smaller ones settle
            // in a single pass. LargeObjectHeapCompactionMode reverts to Default after every blocking
            // GC, so it is set inside the loop rather than once outside it.
            //
            // No GC.WaitForPendingFinalizers here — it was tried, on the theory that the forest's grid
            // source sat finalizable while holding its subgraph, and it moved nothing.
            // Clearing_the_preview_returns_the_retained_heap_at_streamed_cap shows why: the view models
            // release the whole preview on a plain collect, so there is nothing for a finalizer drain
            // to unblock.
            //
            // The argument form is the only legal one: GCCollectionMode.Aggressive throws unless the
            // generation is MaxGeneration and both blocking and compacting are true.
            for (int pass = 0; pass < 2; pass++)
            {
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            }
            watch.Stop();

            GCMemoryInfo after = GC.GetGCMemoryInfo();
            long privateBytes;
            using (Process self = Process.GetCurrentProcess())
                privateBytes = self.PrivateMemorySize64;
            Log.Information(
                "Released memory after closing the dry-run preview in {ElapsedMs}ms: " +
                "GC committed {BeforeMb}MB -> {AfterMb}MB, heap {BeforeHeapMb}MB -> {AfterHeapMb}MB, " +
                "process private now {PrivateMb}MB",
                watch.ElapsedMilliseconds,
                committedBefore >> 20, after.TotalCommittedBytes >> 20,
                heapBefore >> 20, after.HeapSizeBytes >> 20,
                privateBytes >> 20);
        }
        catch (Exception ex)
        {
            // A memory optimization must never be able to take the window down.
            Log.Warning(ex, "Releasing memory after closing the dry-run preview failed");
        }
    }
}
