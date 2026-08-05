using FileManager.Contracts.IPC;
using FileManager.Contracts.Profiles;
using FileManager.UI.Services;
using FileManager.UI.Tests.Fakes;
using FileManager.UI.Tests.TestData;
using FileManager.UI.Undo;
using FileManager.UI.ViewModels;
using FileManager.UI.ViewModels.Editor;

namespace FileManager.UI.Tests;

/// <summary>The profile editor's undo/redo and the unsaved-changes flag that falls out of it.
/// <see cref="ProfileEditorViewModelTests"/> keeps the Profile ↔ draft mapping; this file covers what the
/// editing session itself has to guarantee. Mirrors <see cref="SettingsViewModelUndoTests"/>, because
/// both windows drive the same <see cref="UndoHistory"/>.</summary>
public sealed class ProfileEditorUndoTests
{
    private static (ProfileEditorViewModel Editor, FakeIpcGateway Gateway) NewEditor()
    {
        FakeIpcGateway gateway = new();
        return (new ProfileEditorViewModel(gateway, new FakeFolderPicker()), gateway);
    }

    // ============================ Unsaved changes ============================

    [Fact]
    public void A_freshly_loaded_profile_is_clean_with_nothing_to_undo()
    {
        var (editor, _) = NewEditor();

        editor.Load(ProfileFactory.Sample());

        Assert.False(editor.IsDirty);
        Assert.False(editor.History.CanUndo);
        Assert.False(editor.History.CanRedo);
    }

    [Fact]
    public void A_brand_new_draft_is_clean_with_nothing_to_undo()
    {
        // LoadNew writes every field and adds a source and a target row — all of it baseline state, not
        // edits, so none of it may be steppable.
        var (editor, _) = NewEditor();

        editor.LoadNew();

        Assert.False(editor.IsDirty);
        Assert.False(editor.History.CanUndo);
        Assert.Single(editor.Sources);
        Assert.Single(editor.Targets);
    }

    [Fact]
    public void Clearing_the_editor_leaves_nothing_to_undo()
    {
        var (editor, _) = NewEditor();
        editor.Load(ProfileFactory.Sample());
        editor.ProfileName = "edited";

        editor.Clear();

        Assert.False(editor.IsDirty);
        Assert.False(editor.History.CanUndo);
    }

    [Fact]
    public void Editing_a_field_marks_the_draft_dirty_and_undoing_back_clears_it()
    {
        var (editor, _) = NewEditor();
        Profile original = ProfileFactory.Sample();
        editor.Load(original);

        editor.ProfileName = "renamed";
        Assert.True(editor.IsDirty);

        editor.History.UndoCommand.Execute(null);

        Assert.Equal(original.Name, editor.ProfileName);
        Assert.False(editor.IsDirty);

        editor.History.RedoCommand.Execute(null);
        Assert.Equal("renamed", editor.ProfileName);
        Assert.True(editor.IsDirty);
    }

    [Fact]
    public async Task Saving_clears_the_unsaved_changes_flag_but_keeps_the_history()
    {
        var (editor, _) = NewEditor();
        editor.Load(ProfileFactory.Sample());
        editor.Verbosity = LogVerbosity.FailuresOnly;
        Assert.True(editor.IsDirty);

        await editor.SaveCommand.ExecuteAsync(null);

        Assert.False(editor.IsDirty);
        Assert.True(editor.History.CanUndo);        // still steppable, just no longer unsaved
    }

    [Fact]
    public void Discarding_resets_the_history()
    {
        // Discard reloads the persisted profile, which is a new baseline — there is nothing left to step
        // back through, and nothing to redo either.
        var (editor, _) = NewEditor();
        editor.Load(ProfileFactory.Sample());
        editor.ProfileName = "edited";
        editor.Active = false;

        editor.Discard();

        Assert.False(editor.IsDirty);
        Assert.False(editor.History.CanUndo);
        Assert.False(editor.History.CanRedo);
    }

    // ============================ Coalescing ============================

    [Fact]
    public void Typing_a_name_is_a_single_undo_step()
    {
        var (editor, _) = NewEditor();
        editor.Load(ProfileFactory.Sample());
        string original = editor.ProfileName;

        foreach (string typed in new[] { "N", "Ni", "Nig", "Nightly" })
            editor.ProfileName = typed;

        Assert.Equal(1, editor.History.UndoDepth);
        editor.History.UndoCommand.Execute(null);
        Assert.Equal(original, editor.ProfileName);
        Assert.False(editor.History.CanUndo);
    }

    [Fact]
    public void Typing_into_a_glob_box_is_a_single_undo_step()
    {
        // The editor claims Ctrl+Z from the multi-line glob boxes too, so a typing run there has to
        // collapse the same way — otherwise reverting a pasted-over list would take one press per char.
        var (editor, _) = NewEditor();
        editor.LoadNew();

        editor.ExcludeGlobsText = "*";
        editor.ExcludeGlobsText = "*.t";
        editor.ExcludeGlobsText = "*.tmp";

        Assert.Equal(1, editor.History.UndoDepth);
        editor.History.UndoCommand.Execute(null);
        Assert.Equal("", editor.ExcludeGlobsText);
    }

    [Fact]
    public void A_break_in_the_run_starts_a_new_undo_step()
    {
        // What the view's lost-focus handler does when the user moves to another field and comes back.
        var (editor, _) = NewEditor();
        editor.LoadNew();

        editor.ProfileName = "First";
        editor.History.BreakMerge();
        editor.ProfileName = "Second";

        Assert.Equal(2, editor.History.UndoDepth);
        editor.History.UndoCommand.Execute(null);
        Assert.Equal("First", editor.ProfileName);
    }

    [Fact]
    public void Each_pick_from_a_dropdown_is_its_own_undo_step()
    {
        var (editor, _) = NewEditor();
        editor.LoadNew();

        editor.ConflictResolution = ConflictResolution.Overwrite;
        editor.ConflictResolution = ConflictResolution.OverwriteIfNewer;

        editor.History.UndoCommand.Execute(null);
        Assert.Equal(ConflictResolution.Overwrite, editor.ConflictResolution);
        editor.History.UndoCommand.Execute(null);
        Assert.Equal(ConflictResolution.Skip, editor.ConflictResolution);
    }

    [Fact]
    public void Mirror_deletion_picks_are_undoable()
    {
        var (editor, _) = NewEditor();
        editor.LoadNew();

        editor.MirrorDeletion = MirrorDeletion.Proactive;
        Assert.True(editor.IsDirty);

        editor.History.UndoCommand.Execute(null);
        Assert.Equal(MirrorDeletion.AfterCopy, editor.MirrorDeletion);
        Assert.False(editor.IsDirty);
    }

    [Fact]
    public void Large_file_identity_picks_are_undoable()
    {
        var (editor, _) = NewEditor();
        editor.LoadNew();

        editor.LargeFileIdentity = LargeFileIdentity.SampledHash;
        Assert.True(editor.IsDirty);

        editor.History.UndoCommand.Execute(null);
        Assert.Equal(LargeFileIdentity.FullHash, editor.LargeFileIdentity);
        Assert.False(editor.IsDirty);
    }

    [Fact]
    public void The_identity_threshold_is_undoable_as_one_coalesced_edit()
    {
        // Typed text coalesces (like the filter size boxes), so a keystroke run is one undo step rather
        // than one per character.
        var (editor, _) = NewEditor();
        editor.LoadNew();
        string original = editor.LargeFileIdentityThresholdText;

        editor.LargeFileIdentityThresholdText = "1";
        editor.LargeFileIdentityThresholdText = "10";
        editor.LargeFileIdentityThresholdText = "104";

        editor.History.UndoCommand.Execute(null);
        Assert.Equal(original, editor.LargeFileIdentityThresholdText);
    }

    // ============================ The SyncMode → ScanDestination coupling ============================

    [Fact]
    public void Undoing_a_switch_to_mirror_restores_the_mode_and_the_sweep_flag_together()
    {
        // Mirror forces ScanDestination on. Reversing the mode without also reversing the flag would
        // leave the draft scanning destinations in a mode the user never asked to scan in — which is why
        // the two are recorded as one step.
        var (editor, _) = NewEditor();
        editor.LoadNew();
        Assert.False(editor.ScanDestination);

        editor.SyncMode = SyncMode.Mirror;
        Assert.True(editor.ScanDestination);

        editor.History.UndoCommand.Execute(null);

        Assert.Equal(SyncMode.AdditiveArchive, editor.SyncMode);
        Assert.False(editor.ScanDestination);
        Assert.True(editor.CanEditScanDestination);
        Assert.False(editor.History.CanUndo);       // one step, not two
    }

    [Fact]
    public void Redoing_a_switch_to_mirror_forces_the_sweep_flag_back_on()
    {
        var (editor, _) = NewEditor();
        editor.LoadNew();
        editor.SyncMode = SyncMode.Mirror;
        editor.History.UndoCommand.Execute(null);

        editor.History.RedoCommand.Execute(null);

        Assert.Equal(SyncMode.Mirror, editor.SyncMode);
        Assert.True(editor.ScanDestination);
        Assert.False(editor.CanEditScanDestination);
    }

    [Fact]
    public void Undoing_a_switch_to_mirror_restores_the_remembered_scan_preference()
    {
        // The preference is a plain field the property recorder cannot see, so it needs its own recorded
        // pair. Without it this sequence forgets the checked box: the second Mirror switch stashes the
        // forced "true" as if the user had chosen it, and leaving Mirror never unchecks the flag.
        var (editor, _) = NewEditor();
        editor.LoadNew();
        editor.ScanDestination = true;                 // the user's AdditiveArchive choice

        editor.SyncMode = SyncMode.Mirror;             // stashes true, forces true
        editor.History.UndoCommand.Execute(null);      // back to AdditiveArchive with the box still checked
        Assert.True(editor.ScanDestination);

        editor.ScanDestination = false;                // they change their mind
        editor.SyncMode = SyncMode.Mirror;             // stashes false this time
        editor.SyncMode = SyncMode.AdditiveArchive;

        Assert.False(editor.ScanDestination);
    }

    [Fact]
    public void Loading_a_mirror_profile_records_nothing()
    {
        // Load sets SyncMode and ScanDestination explicitly and must not trip the coupling hook.
        var (editor, _) = NewEditor();

        editor.Load(ProfileFactory.Sample() with { SyncMode = SyncMode.Mirror, ScanDestination = false });

        Assert.True(editor.ScanDestination);            // EffectiveScanDestination, not the stored flag
        Assert.False(editor.IsDirty);
        Assert.False(editor.History.CanUndo);
    }

    // ============================ Source and target rows ============================

    [Fact]
    public void Undo_removes_an_added_source_row()
    {
        var (editor, _) = NewEditor();
        editor.Load(ProfileFactory.Sample());
        int before = editor.Sources.Count;

        editor.AddSourceCommand.Execute(null);
        SourceRowViewModel added = editor.Sources[^1];

        editor.History.UndoCommand.Execute(null);
        Assert.Equal(before, editor.Sources.Count);

        editor.History.RedoCommand.Execute(null);
        Assert.Same(added, editor.Sources[^1]);
    }

    [Fact]
    public void Undo_restores_a_removed_target_row_with_the_edit_it_carried()
    {
        var (editor, _) = NewEditor();
        editor.LoadNew();
        TargetRowViewModel row = Assert.Single(editor.Targets);
        row.Path = @"D:\backup";
        editor.RemoveTargetCommand.Execute(row);
        Assert.Empty(editor.Targets);

        editor.History.UndoCommand.Execute(null);

        TargetRowViewModel restored = Assert.Single(editor.Targets);
        Assert.Same(row, restored);
        Assert.Equal(@"D:\backup", restored.Path);
    }

    [Fact]
    public void A_restored_row_is_undoable_again()
    {
        // Re-inserting re-raises Add, which has to re-attach the per-item tracking — otherwise a row that
        // came back from the dead would silently stop being recorded.
        var (editor, _) = NewEditor();
        editor.LoadNew();
        TargetRowViewModel row = Assert.Single(editor.Targets);
        editor.RemoveTargetCommand.Execute(row);
        editor.History.UndoCommand.Execute(null);

        Assert.Single(editor.Targets).Path = @"E:\again";
        editor.History.UndoCommand.Execute(null);

        Assert.Equal("", editor.Targets[0].Path);
    }

    [Fact]
    public void Loading_rows_is_not_recorded_as_editing_them()
    {
        var (editor, _) = NewEditor();

        editor.Load(ProfileFactory.Sample());

        Assert.Single(editor.Sources);
        Assert.False(editor.History.CanUndo);
        Assert.False(editor.IsDirty);
    }

    [Fact]
    public void Editing_a_loaded_row_is_undoable()
    {
        var (editor, _) = NewEditor();
        editor.Load(ProfileFactory.Sample());
        SourceRowViewModel row = Assert.Single(editor.Sources);

        row.SettleDelaySeconds = 30;
        editor.History.UndoCommand.Execute(null);

        Assert.Equal(7, row.SettleDelaySeconds);        // the sample's value
        Assert.False(editor.IsDirty);
    }

    [Fact]
    public void Rows_dropped_by_a_reload_stop_being_recorded()
    {
        // Load clears the collections. A row left behind by that clear must not keep feeding the history,
        // or an orphan the user can no longer see would dirty the draft.
        var (editor, _) = NewEditor();
        editor.LoadNew();
        SourceRowViewModel orphan = Assert.Single(editor.Sources);

        editor.Load(ProfileFactory.Sample());
        orphan.Path = @"C:\gone";

        Assert.False(editor.IsDirty);
        Assert.False(editor.History.CanUndo);
    }

    // ============================ Confined to the selected profile ============================
    //
    // There is exactly ONE ProfileEditorViewModel for the whole app, reused for every profile, and an
    // undo step captures that view model rather than a profile id. So a step that outlived a load would
    // write the previous profile's value into the new profile's draft. Every loader drops the history
    // (see BeginLoad) and these pin that it stays that way.

    [Fact]
    public void Switching_profiles_after_undoing_back_to_clean_kills_the_redo_branch()
    {
        // The sharp case. IsDirty is position-based now, so undoing back to the loaded state is exactly
        // when the sidebar unlocks and switching profiles becomes possible again — and at that instant the
        // redo stack is still full. If the load did not clear it, one Ctrl+Y on the next profile would
        // apply the previous profile's edit to it.
        var (editor, _) = NewEditor();
        editor.Load(ProfileFactory.Sample());
        editor.ProfileName = "renamed";
        editor.History.UndoCommand.Execute(null);
        Assert.False(editor.IsDirty);                 // navigation is allowed again from here...
        Assert.True(editor.History.CanRedo);          // ...with a live redo branch

        editor.Load(ProfileFactory.Sample() with { Name = "Second" });

        Assert.False(editor.History.CanUndo);
        Assert.False(editor.History.CanRedo);
        editor.History.RedoCommand.Execute(null);
        Assert.Equal("Second", editor.ProfileName);
    }

    [Fact]
    public async Task A_saved_profiles_retained_history_cannot_reach_the_next_profile()
    {
        // A save deliberately KEEPS the history (MarkSaved, not Reset) so the user can step back through
        // what they just saved, and the shell does not re-load the draft afterwards either. That makes the
        // next load the only thing standing between the retained history and a different profile.
        var (editor, _) = NewEditor();
        editor.Load(ProfileFactory.Sample());
        editor.ProfileName = "renamed";
        await editor.SaveCommand.ExecuteAsync(null);
        Assert.True(editor.History.CanUndo);
        Assert.False(editor.IsDirty);

        editor.Load(ProfileFactory.Sample() with { Name = "Next" });
        editor.History.UndoCommand.Execute(null);

        Assert.Equal("Next", editor.ProfileName);
        Assert.False(editor.History.CanUndo);
    }

    [Fact]
    public void A_new_draft_cannot_be_undone_back_into_the_previous_profile()
    {
        var (editor, _) = NewEditor();
        editor.Load(ProfileFactory.Sample());
        editor.ProfileName = "renamed";
        editor.History.UndoCommand.Execute(null);     // leave a redo branch behind too

        editor.LoadNew();
        editor.History.UndoCommand.Execute(null);
        editor.History.RedoCommand.Execute(null);

        Assert.Equal("New Profile", editor.ProfileName);
    }

    [Fact]
    public void A_removed_row_cannot_be_restored_into_a_different_profile()
    {
        // Collection steps are the nastier half: their undo re-inserts the very row instance that was
        // removed, so one surviving into another profile would graft a foreign source row onto it.
        var (editor, _) = NewEditor();
        editor.LoadNew();
        editor.Sources[0].Path = @"C:\first-profile";
        editor.RemoveSourceCommand.Execute(editor.Sources[0]);
        Assert.Empty(editor.Sources);

        editor.Load(ProfileFactory.Sample());
        editor.History.UndoCommand.Execute(null);

        Assert.Single(editor.Sources);                                 // the sample's own row, and only it
        Assert.Equal(@"C:\ui-test\src", editor.Sources[0].Path);
    }

    [Fact]
    public void Every_loader_leaves_both_stacks_empty()
    {
        // Backs BeginLoad. A live redo branch is exactly as dangerous as a live undo one, so assert both
        // for every entry point that repoints the draft — this is the test that fails if someone adds a
        // fourth loader without the reset.
        var (editor, _) = NewEditor();

        foreach (Action load in new Action[]
        {
            () => editor.Load(ProfileFactory.Sample()),
            editor.LoadNew,
            editor.Clear,
        })
        {
            editor.LoadNew();
            editor.ProfileName = "first";
            editor.History.BreakMerge();
            editor.ProfileName = "second";
            editor.History.UndoCommand.Execute(null);
            Assert.True(editor.History.CanUndo);      // both stacks live going in
            Assert.True(editor.History.CanRedo);

            load();

            Assert.Equal(0, editor.History.UndoDepth);
            Assert.Equal(0, editor.History.RedoDepth);
            Assert.False(editor.IsDirty);
        }
    }

    // ============================ What must NOT be undoable ============================

    [Fact]
    public async Task Validation_issues_returned_by_a_save_are_not_undo_steps()
    {
        // Issues is service output, not user state. Recording it would put a step on the stack that the
        // user never made and leave the draft reading as dirty right after a save.
        var (editor, gateway) = NewEditor();
        gateway.SaveResult = new SaveOutcome(false,
            [new ValidationIssue(ValidationSeverity.BlockingWarning, "PROFILE_UNVERIFIED_DELETE", "risky")]);
        editor.Load(ProfileFactory.Sample());
        editor.History.Reset();

        await editor.SaveCommand.ExecuteAsync(null);

        Assert.Single(editor.Issues);
        Assert.False(editor.History.CanUndo);
        Assert.False(editor.IsDirty);
    }

    [Fact]
    public void Switching_which_profile_is_open_is_not_an_undo_step()
    {
        // HasProfile / IsNew describe which profile the editor holds, not an edit to one.
        var (editor, _) = NewEditor();
        editor.Load(ProfileFactory.Sample());

        editor.LoadNew();

        Assert.True(editor.IsNew);
        Assert.False(editor.History.CanUndo);
    }

    [Fact]
    public void The_unsaved_warning_banner_is_not_an_undo_step()
    {
        var (editor, _) = NewEditor();
        editor.Load(ProfileFactory.Sample());

        editor.ShowUnsavedWarning = true;

        Assert.False(editor.IsDirty);
        Assert.False(editor.History.CanUndo);
    }

    [Fact]
    public void Every_declared_undoable_property_round_trips_through_its_own_accessors()
    {
        // Guards a copy-paste slip in an UndoableProperty declaration (a getter reading one property
        // while the setter writes another): reading a value and writing it straight back must be a no-op.
        var (editor, _) = NewEditor();
        editor.Load(ProfileFactory.Sample());

        AssertRoundTrips(editor, nameof(ProfileEditorViewModel));
        foreach (SourceRowViewModel row in editor.Sources)
            AssertRoundTrips(row, nameof(SourceRowViewModel));
        foreach (TargetRowViewModel row in editor.Targets)
            AssertRoundTrips(row, nameof(TargetRowViewModel));

        Assert.False(editor.IsDirty);       // and writing the same values back is not an edit

        static void AssertRoundTrips(IUndoTrackable trackable, string owner)
        {
            foreach (UndoableProperty property in trackable.UndoableProperties)
            {
                object? before = property.Get();
                property.Set(before);
                Assert.True(
                    property.AreEqual(before, property.Get()),
                    $"{owner}.{property.Name} does not read back what was written to it.");
            }
        }
    }
}
