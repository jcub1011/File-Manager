using FileManager.Contracts.DryRun;
using FileManager.UI.Services;
using FileManager.UI.ViewModels;
using System;
using System.IO;

namespace FileManager.UI.Tests;

/// <summary>The right-click clipboard/open commands on the dry-run list rows: they route the right
/// path/name to the actions service, and Open File / reveal grey out (CanExecute) when the target
/// isn't on disk — the "greyed out when not a real file" behavior, decided by a live disk check.</summary>
public sealed class DryRunItemActionsCommandsTests
{
    private sealed class RecordingActions : IDryRunItemActions
    {
        public string? Copied;
        public string? Opened;
        public string? Revealed;
        public string? OpenedFolder;
        public void CopyText(string? text) => Copied = text;
        public void OpenFile(string path) => Opened = path;
        public void RevealInExplorer(string path) => Revealed = path;
        public void OpenFolderInExplorer(string path) => OpenedFolder = path;
    }

    private static DryRunFileRow FileRow(string dir, string name, IDryRunItemActions actions) =>
        new(dir, name, null, OperationKind.Processed, null, null, Array.Empty<DryRunTargetRow>()) { Actions = actions };

    [Fact]
    public void Copy_commands_send_path_name_and_stem_to_the_service()
    {
        RecordingActions actions = new();
        DryRunFileRow row = FileRow(@"C:\data\reports", "annual.report.pdf", actions);

        row.CopyPathCommand.Execute(null);
        Assert.Equal(Path.Join(@"C:\data\reports", "annual.report.pdf"), actions.Copied);

        row.CopyNameCommand.Execute(null);
        Assert.Equal("annual.report.pdf", actions.Copied);

        row.CopyNameWithoutExtensionCommand.Execute(null);
        Assert.Equal("annual.report", actions.Copied);
    }

    [Fact]
    public void Open_commands_are_enabled_and_route_the_path_when_the_file_exists()
    {
        string dir = Path.Combine(Path.GetTempPath(), "fm-ctx-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string name = "present.txt";
        string full = Path.Combine(dir, name);
        File.WriteAllText(full, "x");
        try
        {
            RecordingActions actions = new();
            DryRunFileRow row = FileRow(dir, name, actions);

            Assert.True(row.OpenFileCommand.CanExecute(null));
            Assert.True(row.RevealInExplorerCommand.CanExecute(null));

            row.OpenFileCommand.Execute(null);
            Assert.Equal(Path.Join(dir, name), actions.Opened);
            row.RevealInExplorerCommand.Execute(null);
            Assert.Equal(Path.Join(dir, name), actions.Revealed);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Open_commands_are_disabled_for_a_planned_file_not_on_disk()
    {
        RecordingActions actions = new();
        DryRunFileRow row = FileRow(@"C:\nope\does\not\exist", "planned.bin", actions);

        Assert.False(row.OpenFileCommand.CanExecute(null));
        Assert.False(row.RevealInExplorerCommand.CanExecute(null));
    }

    [Theory]
    [InlineData("report.pdf", true)]   // a stem before the extension
    [InlineData("README", true)]       // no extension at all — the whole name is the stem
    [InlineData(".git", false)]        // all extension, empty stem — nothing to copy
    [InlineData(".demo", false)]
    public void Copy_name_without_extension_is_disabled_for_all_extension_dotfiles(string name, bool enabled)
    {
        DryRunFileRow row = FileRow(@"C:\data", name, new RecordingActions());
        Assert.Equal(enabled, row.CopyNameWithoutExtensionCommand.CanExecute(null));
    }
}
