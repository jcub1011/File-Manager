using FileManager.Contracts.IPC;
using FileManager.Contracts.Profiles;
using FileManager.UI.Services;
using FileManager.UI.Tests.Fakes;
using FileManager.UI.Tests.TestData;
using FileManager.UI.ViewModels;

namespace FileManager.UI.Tests;

public sealed class ProfileEditorViewModelTests
{
    private static (ProfileEditorViewModel Editor, FakeIpcGateway Gateway) NewEditor()
    {
        FakeIpcGateway gateway = new();
        return (new ProfileEditorViewModel(gateway, new FakeFolderPicker()), gateway);
    }

    [Fact]
    public void New_profile_defaults_scan_destination_off_and_editable()
    {
        var (editor, _) = NewEditor();
        editor.LoadNew();
        Assert.False(editor.ScanDestination);
        Assert.True(editor.CanEditScanDestination);
        Assert.False(editor.BuildProfile().ScanDestination);
    }

    [Fact]
    public void Mirror_forces_scan_destination_on_and_locks_the_field()
    {
        var (editor, _) = NewEditor();
        editor.LoadNew();
        editor.SyncMode = SyncMode.Mirror;

        Assert.True(editor.ScanDestination);
        Assert.False(editor.CanEditScanDestination);
        Assert.True(editor.BuildProfile().ScanDestination);
    }

    [Fact]
    public void Switching_mirror_back_to_additive_restores_the_scan_destination_preference()
    {
        var (editor, _) = NewEditor();
        editor.LoadNew();                       // AdditiveArchive, scan off

        // Mirror forces the sweep on and locks the field...
        editor.SyncMode = SyncMode.Mirror;
        Assert.True(editor.ScanDestination);

        // ...and switching back must NOT leave it stuck on — the AdditiveArchive preference (off) returns.
        editor.SyncMode = SyncMode.AdditiveArchive;
        Assert.False(editor.ScanDestination);
        Assert.True(editor.CanEditScanDestination);
        Assert.False(editor.BuildProfile().ScanDestination);
    }

    [Fact]
    public void Switching_mirror_back_to_additive_preserves_an_explicit_scan_on_preference()
    {
        var (editor, _) = NewEditor();
        editor.LoadNew();
        editor.ScanDestination = true;          // the user opted the AdditiveArchive sweep on

        editor.SyncMode = SyncMode.Mirror;      // forced on either way
        Assert.True(editor.ScanDestination);

        editor.SyncMode = SyncMode.AdditiveArchive;
        Assert.True(editor.ScanDestination);    // their explicit choice is restored, not cleared
    }

    [Fact]
    public void Scan_destination_round_trips_through_load_and_build_in_additive()
    {
        var (editor, _) = NewEditor();
        Profile original = ProfileFactory.Sample() with
        {
            SyncMode = SyncMode.AdditiveArchive,
            ScanDestination = true,
        };

        editor.Load(original);
        Assert.True(editor.ScanDestination);
        Assert.True(editor.CanEditScanDestination);
        Assert.True(editor.BuildProfile().ScanDestination);
    }

    [Fact]
    public void TryBuildDraft_returns_the_current_draft_when_fields_parse()
    {
        var (editor, _) = NewEditor();
        editor.LoadNew();
        editor.ProfileName = "Experiment";

        Assert.True(editor.TryBuildDraft(out Profile? draft, out string? error));
        Assert.Null(error);
        Assert.NotNull(draft);
        Assert.Equal("Experiment", draft!.Name);
    }

    [Fact]
    public void TryBuildDraft_fails_fast_on_an_invalid_numeric_field()
    {
        var (editor, _) = NewEditor();
        editor.LoadNew();
        editor.MaxDepthText = "not-a-number";

        Assert.False(editor.TryBuildDraft(out Profile? draft, out string? error));
        Assert.Null(draft);
        Assert.NotNull(error);
    }

    [Fact]
    public void New_profile_gets_the_spec_defaults()
    {
        var (editor, _) = NewEditor();
        editor.LoadNew();
        Profile draft = editor.BuildProfile();

        Assert.Equal(2, draft.SchemaVersion);
        Assert.NotEqual(Guid.Empty, draft.Id);
        Assert.True(draft.Triggers.ManualShell);
        Assert.False(draft.Triggers.Watcher);
        Assert.Null(draft.Triggers.Schedule);
        Assert.Null(draft.Transformers);
        Assert.Equal(ConflictResolution.Skip, draft.Policies.ConflictResolution);
        Assert.Equal(VerificationMethod.XxHash128, draft.Policies.VerificationMethod);
        Assert.Equal(OnSuccessAction.KeepSource, draft.Policies.OnSuccess);
        Assert.Equal(OverwriteHandling.StageOverwrites, draft.Policies.OverwriteHandling);
        SourceConfig source = Assert.Single(draft.Sources);
        Assert.Equal(2, source.SettleDelaySeconds);
        Assert.Equal(500, source.StabilityIntervalMs);
        Assert.False(editor.IsDirty);
    }

    [Fact]
    public void Load_then_build_round_trips_the_fields_the_editor_does_not_own()
    {
        var (editor, _) = NewEditor();
        Profile original = ProfileFactory.Sample();
        editor.Load(original);
        Profile rebuilt = editor.BuildProfile();

        Assert.Equal(original.Id, rebuilt.Id);
        Assert.Equal(original.SchemaVersion, rebuilt.SchemaVersion);
        Assert.Equal(original.Triggers, rebuilt.Triggers);           // watcher + schedule preserved
        Assert.Equal(original.Transformers, rebuilt.Transformers);   // same reference, untouched
        Assert.Equal(original.Filters!.IncludeRegex, rebuilt.Filters!.IncludeRegex);
        Assert.Equal(original.Sources[0].SettleDelaySeconds, rebuilt.Sources[0].SettleDelaySeconds);
        Assert.Equal(original.Policies, rebuilt.Policies);
        Assert.Equal(original.Logging, rebuilt.Logging);
        Assert.Equal(original.Filters.Include, rebuilt.Filters.Include);
        Assert.Equal(original.Filters.MinSizeBytes, rebuilt.Filters.MinSizeBytes);
        Assert.Equal(original.Filters.MaxDepth, rebuilt.Filters.MaxDepth);
        Assert.False(editor.IsDirty, "loading must not mark the draft dirty");
    }

    [Fact]
    public void Editing_marks_dirty_and_glob_lines_parse()
    {
        var (editor, _) = NewEditor();
        editor.LoadNew();

        editor.IncludeGlobsText = " *.wav \n\n  *.flac\n";
        Assert.True(editor.IsDirty);

        Profile draft = editor.BuildProfile();
        Assert.Equal(new[] { "*.wav", "*.flac" }, draft.Filters!.Include);
    }

    [Fact]
    public void Empty_filter_fields_collapse_to_a_null_filter_set()
    {
        var (editor, _) = NewEditor();
        editor.LoadNew();
        Assert.Null(editor.BuildProfile().Filters);
    }

    [Fact]
    public void Unparseable_size_is_a_local_error_and_never_reaches_the_gateway()
    {
        var (editor, gateway) = NewEditor();
        editor.LoadNew();
        editor.MinSizeText = "not-a-number";

        editor.SaveCommand.Execute(null);

        Assert.NotNull(editor.LocalError);
        Assert.Empty(gateway.SaveCalls);
    }

    [Fact]
    public async Task Successful_save_clears_dirty_and_notifies()
    {
        var (editor, gateway) = NewEditor();
        Guid? savedId = null;
        editor.Saved = id => savedId = id;
        editor.LoadNew();
        editor.ProfileName = "My Profile";

        await editor.SaveAsync();

        var call = Assert.Single(gateway.SaveCalls);
        Assert.False(call.Acknowledge);
        Assert.Equal("My Profile", call.Profile.Name);
        Assert.False(editor.IsDirty);
        Assert.Equal(call.Profile.Id, savedId);
    }

    [Fact]
    public async Task Rejected_save_shows_issues_and_gates_the_acknowledge_button()
    {
        var (editor, gateway) = NewEditor();
        editor.LoadNew();

        // Blocking warning only → acknowledge path opens.
        gateway.SaveResult = new SaveOutcome(false,
            [new ValidationIssue(ValidationSeverity.BlockingWarning, "PROFILE_UNVERIFIED_DELETE", "risky")]);
        await editor.SaveAsync();
        Assert.True(editor.CanAcknowledgeAndSave);

        // Any Error → acknowledge path stays closed.
        gateway.SaveResult = new SaveOutcome(false,
        [
            new ValidationIssue(ValidationSeverity.BlockingWarning, "PROFILE_UNVERIFIED_DELETE", "risky"),
            new ValidationIssue(ValidationSeverity.Error, "PROFILE_NO_TARGETS", "no targets"),
        ]);
        await editor.SaveAsync();
        Assert.False(editor.CanAcknowledgeAndSave);
        Assert.Equal(2, editor.Issues.Count);
    }

    [Fact]
    public async Task Acknowledge_and_save_resends_with_the_flag()
    {
        var (editor, gateway) = NewEditor();
        editor.LoadNew();

        await editor.AcknowledgeAndSaveAsync();

        var call = Assert.Single(gateway.SaveCalls);
        Assert.True(call.Acknowledge);
    }

    [Fact]
    public void Archive_folder_visibility_follows_the_on_success_action()
    {
        var (editor, _) = NewEditor();
        editor.LoadNew();
        Assert.False(editor.ShowArchiveFolder);
        editor.OnSuccess = OnSuccessAction.MoveToArchive;
        Assert.True(editor.ShowArchiveFolder);
    }

    [Fact]
    public void Reserved_enum_members_are_not_offered()
    {
        var (editor, _) = NewEditor();
        Assert.DoesNotContain(VerificationMethod.SizeTimestamp, editor.VerificationOptions);
    }

    [Fact]
    public void Mirror_sync_mode_is_offered()
    {
        // Mirror is selectable now: the dry run fully previews it (executor deletion is a follow-up).
        var (editor, _) = NewEditor();
        Assert.Contains(SyncMode.Mirror, editor.SyncModeOptions);
    }

    [Fact]
    public void Discard_restores_the_loaded_profile()
    {
        var (editor, _) = NewEditor();
        Profile original = ProfileFactory.Sample();
        editor.Load(original);
        editor.ProfileName = "mutated";
        Assert.True(editor.IsDirty);

        editor.Discard();

        Assert.Equal(original.Name, editor.ProfileName);
        Assert.False(editor.IsDirty);
    }
}
