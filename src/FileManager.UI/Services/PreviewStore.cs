using FileManager.Contracts.IPC;
using Serilog;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace FileManager.UI.Services;

/// <summary>A preview result the user can come back to: the plan a run froze, remembered by profile so
/// navigating away does not destroy it.
///
/// <para><b>Metadata only — never rows.</b> The <see cref="Planned"/> event carries the counts, the byte
/// totals, the blocking warnings and the timestamp, which is everything needed to describe the result and
/// re-fetch it. The rows themselves stay where the service already put them, in the run's snapshot
/// directory, and are re-streamed by <c>get-run-plan-stream</c> when the user actually looks. That split is
/// the whole reason several results can be retained at once: a materialized preview costs ~200–350 MB of
/// process footprint at scale, while this record costs a few hundred bytes.</para></summary>
/// <param name="Planned">The <c>run-planned</c> event, replayed verbatim into
/// <c>DryRunViewModel.LoadPlanAsync</c> — so a restored preview goes through the identical ingest path as a
/// fresh one, rather than a second code path that could disagree with it.</param>
/// <param name="TakenAtUtc">When the plan was frozen (the event's own <c>AtUtc</c>). What staleness is
/// measured against.</param>
public sealed record StoredPreview(RunPlannedEvent Planned, DateTimeOffset TakenAtUtc)
{
    public Guid RunId => Planned.RunId;
}

/// <summary>Remembers the most recent preview per profile, so re-opening the Preview tab shows the result
/// again instead of an empty state.
///
/// <para><b>What this changes about run lifetime.</b> Before this, a preview was declined the moment the
/// user looked away — <c>DryRunViewModel</c> called <c>AbandonPendingRun()</c> on every profile switch —
/// because a run parked in <c>AwaitingApproval</c> holds a snapshot directory with no expiry of its own.
/// Retaining results means deliberately keeping those runs parked, so this type owns the obligation that
/// came with it: <b>every entry it drops must decline the run it was holding</b>, and
/// <see cref="DeclineAllAsync"/> must run before the window closes. One entry per profile is what bounds
/// the total — a re-preview of the same profile replaces (and declines) its own previous entry.</para>
///
/// <para><b>Results do not survive a service restart.</b> <c>EngineHost</c> sweeps the runs directory at
/// startup and the coordinator's run table is in-memory, so every parked run is gone. A restore that comes
/// back <c>RUN_NOT_FOUND</c> is therefore the NORMAL case after a restart, not an error — callers drop the
/// entry and show the ordinary empty state.</para></summary>
public sealed class PreviewStore(IIpcGateway gateway)
{
    // Keyed by profile id. Guid.Empty stands in for a never-saved draft, which has no persisted id yet but
    // is still previewable — one such draft can be open at a time, so one slot is enough.
    private readonly Dictionary<Guid, StoredPreview> _byProfile = [];

    /// <summary>How many results are retained. Bounded by the profile count, so this is a diagnostic
    /// rather than a limit.</summary>
    public int Count => _byProfile.Count;

    public StoredPreview? For(Guid? profileId) =>
        _byProfile.TryGetValue(profileId ?? Guid.Empty, out StoredPreview? stored) ? stored : null;

    /// <summary>Remembers a finished plan for its profile, declining whatever that profile was holding.
    /// <para>The displaced run is declined rather than merely forgotten: it is parked in
    /// <c>AwaitingApproval</c> holding a snapshot directory, and nothing else will ever answer for it.</para></summary>
    public void Remember(Guid? profileId, StoredPreview preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        Guid key = profileId ?? Guid.Empty;
        if (_byProfile.TryGetValue(key, out StoredPreview? previous) && previous.RunId != preview.RunId)
            Decline(previous.RunId);
        _byProfile[key] = preview;
    }

    /// <summary>Forgets this profile's result and declines its run. Returns the run id that was dropped,
    /// so a caller can stop recognizing its events as its own.</summary>
    public Guid? Forget(Guid? profileId)
    {
        Guid key = profileId ?? Guid.Empty;
        if (!_byProfile.Remove(key, out StoredPreview? stored))
            return null;
        Decline(stored.RunId);
        return stored.RunId;
    }

    /// <summary>Forgets an entry WITHOUT declining its run — for a run that is already gone (a
    /// <c>RUN_NOT_FOUND</c> restore, or one whose <c>run-completed</c> has arrived).
    /// <para>Separate from <see cref="Forget"/> on purpose: declining a closed run answers
    /// RUN_NOT_APPROVABLE, and while that is harmless it would put a failure in the log for the most
    /// ordinary path there is — reopening a preview after the service restarted.</para></summary>
    public void Drop(Guid runId)
    {
        foreach ((Guid profileId, StoredPreview stored) in _byProfile)
        {
            if (stored.RunId == runId)
            {
                _byProfile.Remove(profileId);
                return;
            }
        }
    }

    /// <summary>Discards every retained run and empties the store. For window close.
    /// <para>Awaited, unlike the individual discards: this is the last chance to release the snapshot
    /// directories, and a fire-and-forget here would race the process exit. Failures are logged and
    /// swallowed — a service that has already gone away needs no telling.</para></summary>
    public async Task DeclineAllAsync()
    {
        if (_byProfile.Count == 0)
            return;
        List<Guid> runIds = [];
        foreach (StoredPreview stored in _byProfile.Values)
            runIds.Add(stored.RunId);
        _byProfile.Clear();

        foreach (Guid runId in runIds)
        {
            try
            {
                var discarded = await gateway.DiscardRunAsync(runId);
                if (discarded.TryGetError(out IpcError? error))
                    Log.Debug("Discarding retained run {RunId} on close failed: {Message}", runId, error.Message);
            }
            catch (Exception ex)
            {
                // Last resort: closing the window must never be blocked by cleanup of a run the service
                // may already have forgotten.
                Log.Debug(ex, "Discarding retained run {RunId} on close failed", runId);
            }
        }
    }

    /// <summary>Fire-and-forget DISCARD for a superseded or abandoned entry.
    ///
    /// <para><b>Discard, not decline.</b> Declining merely closes a run, and a closed run is now retained
    /// until the user gets rid of it — so a window that declined every superseded preview would fill the
    /// job queue with rows for previews the user replaced by pressing Preview again and never asked to
    /// keep a record of. Discard both closes it and removes it, which is what abandoning a preview
    /// actually means.</para>
    ///
    /// <para>The caller is mid-transition and an already-gone run answering RUN_NOT_FOUND is a normal race,
    /// not a fault — the same contract <c>DryRunViewModel.AbandonPendingRun</c> documents.</para></summary>
    private void Decline(Guid runId) =>
        _ = gateway.DiscardRunAsync(runId)
            .ContinueWith(
                t => Log.Debug(t.Exception, "Discarding superseded retained run {RunId} failed", runId),
                System.Threading.CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
}
