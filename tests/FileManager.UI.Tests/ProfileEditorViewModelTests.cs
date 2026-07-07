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
        Assert.Equal(VerificationMethod.Sha256, draft.Policies.VerificationMethod);
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
        Assert.DoesNotContain(SyncMode.Mirror, editor.SyncModeOptions);
        Assert.DoesNotContain(VerificationMethod.SizeTimestamp, editor.VerificationOptions);
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
