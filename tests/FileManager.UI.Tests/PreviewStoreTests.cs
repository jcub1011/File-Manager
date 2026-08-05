using FileManager.Contracts.IPC;
using FileManager.UI.Services;
using FileManager.UI.Tests.Fakes;
using FileManager.UI.Tests.TestData;

namespace FileManager.UI.Tests;

/// <summary>The retained-preview store, and above all the obligation it inherited.
///
/// <para>A run parked in <c>AwaitingApproval</c> holds a snapshot directory and has no expiry of its own,
/// so before previews were retained, <c>DryRunViewModel</c> declined one on every profile switch. Retaining
/// results deliberately keeps those runs parked — which means the ONLY thing standing between this feature
/// and a directory leak for the lifetime of the service is that this type gets rid of every entry it drops.
/// Most of what follows tests that, not the dictionary.</para>
///
/// <para><b>DISCARD, not decline.</b> Declining merely CLOSES a run, and a closed run is now retained until
/// something removes it — so a window that declined every superseded preview would fill the job queue with
/// rows for previews the user replaced and never asked to keep. These assertions pin the discard.</para></summary>
public sealed class PreviewStoreTests
{
    private static StoredPreview Preview(Guid runId, Guid profileId, DateTimeOffset? at = null)
    {
        DateTimeOffset taken = at ?? DateTimeOffset.UnixEpoch;
        return new StoredPreview(RunPlans.Planned(runId, profileId, plannedAtUtc: taken), taken);
    }

    [Fact]
    public void A_remembered_preview_comes_back_for_its_profile()
    {
        FakeIpcGateway gateway = new();
        PreviewStore store = new(gateway);
        Guid profileId = Guid.NewGuid(), runId = Guid.NewGuid();

        store.Remember(profileId, Preview(runId, profileId));

        Assert.Equal(runId, store.For(profileId)?.RunId);
        Assert.Null(store.For(Guid.NewGuid()));
    }

    /// <summary>The whole point of keying by profile: previewing B must not destroy A's result. Before
    /// this, every new preview answered whatever the previous one left parked, regardless of profile.</summary>
    [Fact]
    public void Remembering_one_profile_leaves_another_profiles_result_alone()
    {
        FakeIpcGateway gateway = new();
        PreviewStore store = new(gateway);
        Guid profileA = Guid.NewGuid(), profileB = Guid.NewGuid();
        Guid runA = Guid.NewGuid(), runB = Guid.NewGuid();

        store.Remember(profileA, Preview(runA, profileA));
        store.Remember(profileB, Preview(runB, profileB));

        Assert.Equal(runA, store.For(profileA)?.RunId);
        Assert.Equal(runB, store.For(profileB)?.RunId);
        Assert.Empty(gateway.DiscardRunCalls);   // neither superseded the other
        Assert.Equal(2, store.Count);
    }

    /// <summary>THE leak guard. Re-previewing a profile replaces its entry, and the run the old entry held
    /// must be discarded — nothing else will ever answer for it, and merely closing it would leave a row in
    /// the queue for a preview the user superseded.</summary>
    [Fact]
    public void Re_previewing_a_profile_discards_the_run_its_previous_result_was_holding()
    {
        FakeIpcGateway gateway = new();
        PreviewStore store = new(gateway);
        Guid profileId = Guid.NewGuid();
        Guid first = Guid.NewGuid(), second = Guid.NewGuid();

        store.Remember(profileId, Preview(first, profileId));
        store.Remember(profileId, Preview(second, profileId));

        Assert.Equal(first, Assert.Single(gateway.DiscardRunCalls));
        Assert.Equal(second, store.For(profileId)?.RunId);
        Assert.Equal(1, store.Count);
    }

    /// <summary>Re-remembering the SAME run (a restore that re-registers what is already held) must not
    /// discard it — that would destroy the very run being restored.</summary>
    [Fact]
    public void Remembering_the_same_run_again_discards_nothing()
    {
        FakeIpcGateway gateway = new();
        PreviewStore store = new(gateway);
        Guid profileId = Guid.NewGuid(), runId = Guid.NewGuid();

        store.Remember(profileId, Preview(runId, profileId));
        store.Remember(profileId, Preview(runId, profileId));

        Assert.Empty(gateway.DiscardRunCalls);
        Assert.Equal(runId, store.For(profileId)?.RunId);
    }

    [Fact]
    public void Forgetting_a_profile_discards_the_run_it_was_holding()
    {
        FakeIpcGateway gateway = new();
        PreviewStore store = new(gateway);
        Guid profileId = Guid.NewGuid(), runId = Guid.NewGuid();
        store.Remember(profileId, Preview(runId, profileId));

        Guid? forgotten = store.Forget(profileId);

        Assert.Equal(runId, forgotten);
        Assert.Equal(runId, Assert.Single(gateway.DiscardRunCalls));
        Assert.Null(store.For(profileId));
    }

    /// <summary>Drop is for a run that is ALREADY gone — a RUN_NOT_FOUND restore, or one whose
    /// run-completed arrived. It must not discard, or the most ordinary path in the feature (reopening a
    /// preview after the service restarted) logs a failure every time.</summary>
    [Fact]
    public void Dropping_a_run_that_is_already_gone_does_not_try_to_discard_it()
    {
        FakeIpcGateway gateway = new();
        PreviewStore store = new(gateway);
        Guid profileId = Guid.NewGuid(), runId = Guid.NewGuid();
        store.Remember(profileId, Preview(runId, profileId));

        store.Drop(runId);

        Assert.Empty(gateway.DiscardRunCalls);
        Assert.Null(store.For(profileId));
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void Dropping_an_unknown_run_is_a_no_op()
    {
        FakeIpcGateway gateway = new();
        PreviewStore store = new(gateway);
        Guid profileId = Guid.NewGuid();
        store.Remember(profileId, Preview(Guid.NewGuid(), profileId));

        store.Drop(Guid.NewGuid());

        Assert.Equal(1, store.Count);
    }

    /// <summary>Window close. Every retained run must be discarded — this is the last moment anything can
    /// release those snapshot directories, and the process may exit immediately afterwards.</summary>
    [Fact]
    public async Task Closing_discards_every_retained_run()
    {
        FakeIpcGateway gateway = new();
        PreviewStore store = new(gateway);
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), c = Guid.NewGuid();
        store.Remember(Guid.NewGuid(), Preview(a, Guid.NewGuid()));
        store.Remember(Guid.NewGuid(), Preview(b, Guid.NewGuid()));
        store.Remember(Guid.NewGuid(), Preview(c, Guid.NewGuid()));

        await store.DeclineAllAsync();

        Assert.Equal(3, gateway.DiscardRunCalls.Count);
        Assert.Contains(a, gateway.DiscardRunCalls);
        Assert.Contains(b, gateway.DiscardRunCalls);
        Assert.Contains(c, gateway.DiscardRunCalls);
        Assert.Equal(0, store.Count);
    }

    /// <summary>A service that has already gone away needs no telling, and must not trap the user in the
    /// window. The discards are best-effort by contract.</summary>
    [Fact]
    public async Task Closing_survives_a_service_that_refuses_every_discard()
    {
        FakeIpcGateway gateway = new()
        {
            DiscardRunResult = new IpcError("IPC_TRANSPORT", "the pipe is gone"),
        };
        PreviewStore store = new(gateway);
        store.Remember(Guid.NewGuid(), Preview(Guid.NewGuid(), Guid.NewGuid()));

        await store.DeclineAllAsync();   // must not throw

        Assert.Equal(0, store.Count);
    }

    /// <summary>A never-saved draft has no persisted id yet but is still previewable. Guid.Empty is its
    /// slot, and a null key must reach the same one — otherwise its result is stored under a key nothing
    /// ever reads back.</summary>
    [Fact]
    public void A_never_saved_draft_stores_under_one_stable_slot()
    {
        FakeIpcGateway gateway = new();
        PreviewStore store = new(gateway);
        Guid runId = Guid.NewGuid();

        store.Remember(null, Preview(runId, Guid.Empty));

        Assert.Equal(runId, store.For(null)?.RunId);
        Assert.Equal(runId, store.For(Guid.Empty)?.RunId);
    }
}
