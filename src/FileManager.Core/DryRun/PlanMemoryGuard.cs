using System;

namespace FileManager.Core.DryRun;

/// <summary>What replaced the plan's file-count cap on the memory side.
///
/// <para>A count was never the thing anyone cared about — it was a proxy for "this plan will exhaust the
/// service". A bad proxy, too: 500,000 tiny files under one directory cost a fraction of 50,000 deep ones,
/// and the count told a user nothing about which had happened. Worse, it failed by <em>truncating</em>,
/// and a truncated plan is the work list a run then executes, so the failure mode was a wrong job rather
/// than a refused one (<c>MirrorDeletionPass</c> refuses a truncated plan outright, which is how "too many
/// files" became "this profile cannot be mirrored").</para>
///
/// <para>So this measures the resource instead, and fails the plan. Failing is the whole point: a plan
/// that stops short is dangerous, a plan that says "the machine ran out of memory at 3.2M files" is
/// merely unfinished, and the user can act on it.</para>
///
/// <para><b>Two conditions, both required.</b> The runtime's own high-load line
/// (<see cref="GCMemoryInfo.HighMemoryLoadThresholdBytes"/>) says the machine is under pressure — but on
/// a box where some other process is the hog, that would kill a plan this service could have finished
/// comfortably. So it also has to be true that we are a material contributor, i.e. our own managed heap
/// is past <paramref name="processHeapFloorBytes"/>. Below that floor the pressure is somebody else's and
/// the plan runs on.</para>
///
/// <para>Sampled every <see cref="CheckEveryFiles"/> files rather than per file:
/// <c>GC.GetGCMemoryInfo()</c> is not free, and the quantity it reports cannot move meaningfully inside a
/// few thousand rows. The counter is the plan's own running source+destination total, so the interval is
/// work-proportional rather than wall-clock — a plan blocked on a slow share does not spin on this.</para>
///
/// <para>Not thread-safe, and does not need to be: <c>ProfilePlanner</c> consults it between chunks on
/// the single iterator thread.</para></summary>
internal sealed class PlanMemoryGuard(long processHeapFloorBytes)
{
    /// <summary>Files between samples. Large enough that the check is free at any plan size, small
    /// enough that the overshoot between two samples cannot itself be what exhausts the heap.</summary>
    internal const int CheckEveryFiles = 65_536;

    private long _nextCheckAt = CheckEveryFiles;

    /// <summary>Null while the plan may continue; otherwise the message the plan fails with. Takes the
    /// files covered so far so the message can say where it stopped — "at 3,200,000 files" is what makes
    /// this actionable, and it is the number the count-cap error never had a reason to report.</summary>
    public string? Check(long filesSoFar)
    {
        if (filesSoFar < _nextCheckAt)
            return null;
        _nextCheckAt = filesSoFar + CheckEveryFiles;

        GCMemoryInfo info = GC.GetGCMemoryInfo();
        // Zero when the runtime cannot determine a threshold (it does not on every host/config). No
        // threshold means no opinion, so the guard stands down rather than guessing one.
        if (info.HighMemoryLoadThresholdBytes <= 0)
            return null;
        if (info.MemoryLoadBytes < info.HighMemoryLoadThresholdBytes)
            return null;
        if (info.HeapSizeBytes < processHeapFloorBytes)
            return null;   // the pressure is not ours to relieve

        return $"the machine ran out of memory while planning, at {filesSoFar:N0} files " +
            $"(the plan had reached {info.HeapSizeBytes >> 20:N0} MB and the system is at " +
            $"{info.MemoryLoadBytes >> 20:N0} MB of {info.TotalAvailableMemoryBytes >> 20:N0} MB). " +
            "Narrow the profile's sources, or run it in smaller scopes.";
    }
}
