using FileManager.Contracts.DryRun;
using FileManager.Contracts.IPC;
using FileManager.UI.Services;
using FileManager.UI.Tests.Fakes;
using FileManager.UI.ViewModels;

namespace FileManager.UI.Tests;

public sealed class DryRunViewModelTests
{
    private static (DryRunViewModel ViewModel, FakeIpcGateway Gateway) NewViewModel()
    {
        FakeIpcGateway gateway = new();
        DryRunViewModel viewModel = new(gateway, new FakeFolderPicker());
        viewModel.SetProfile(Guid.NewGuid(), "P");
        return (viewModel, gateway);
    }

    private static DryRunReport SampleReport(Guid profileId) => new(profileId, DateTimeOffset.UnixEpoch,
    [
        new DryRunFileResult
        {
            SourcePath = @"C:\s\fresh.txt",
            Disposition = DryRunFileDisposition.WouldProcess,
            SourceDisposition = "KeepSource",
            Targets = [new DryRunTargetAction { TargetPath = @"C:\t\fresh.txt", Kind = DryRunTargetKind.WouldWrite }],
        },
        new DryRunFileResult
        {
            SourcePath = @"C:\s\clobber.txt",
            Disposition = DryRunFileDisposition.WouldProcess,
            SourceDisposition = "MoveToTrash",
            Targets =
            [
                new DryRunTargetAction { TargetPath = @"C:\t\clobber.txt", Kind = DryRunTargetKind.WouldOverwrite },
                new DryRunTargetAction { TargetPath = @"C:\t2\clobber.txt", Kind = DryRunTargetKind.WouldRenameTo, Detail = @"C:\t2\clobber (1).txt" },
            ],
        },
        new DryRunFileResult
        {
            SourcePath = @"C:\s\junk.tmp",
            Disposition = DryRunFileDisposition.WouldSkipFilter,
            DecidingFilter = "Exclude pattern glob *.tmp",
        },
        new DryRunFileResult
        {
            SourcePath = @"C:\s\same.txt",
            Disposition = DryRunFileDisposition.WouldSkipUnchanged,
            Targets = [new DryRunTargetAction { TargetPath = @"C:\t\same.txt", Kind = DryRunTargetKind.WouldSkipUnchanged }],
        },
    ]);

    [Fact]
    public async Task Summary_counts_reflect_the_report()
    {
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = SampleReport(viewModel.ProfileId!.Value);

        await viewModel.RunAsync(CancellationToken.None);

        Assert.True(viewModel.HasReport);
        Assert.Equal(4, viewModel.TotalFiles);
        Assert.Equal(2, viewModel.ProcessCount);
        Assert.Equal(1, viewModel.FilterSkipCount);
        Assert.Equal(1, viewModel.UnchangedSkipCount);
        Assert.Equal(1, viewModel.OverwriteCount);
        Assert.Equal(1, viewModel.RenameCount);
        Assert.Equal(1, viewModel.DisposalCount);   // MoveToTrash; KeepSource is not a disposal
        Assert.True(viewModel.HasDestructiveActions);
    }

    [Fact]
    public async Task Groups_split_by_disposition()
    {
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = SampleReport(viewModel.ProfileId!.Value);

        await viewModel.RunAsync(CancellationToken.None);

        Assert.Equal(2, viewModel.ProcessFiles.Count);
        Assert.Single(viewModel.FilterSkips);
        Assert.Single(viewModel.UnchangedSkips);
        Assert.Contains("*.tmp", viewModel.FilterSkips[0].DecidingFilter);
    }

    [Fact]
    public async Task Destructive_only_filters_to_overwrites_and_disposals()
    {
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = SampleReport(viewModel.ProfileId!.Value);
        await viewModel.RunAsync(CancellationToken.None);

        viewModel.DestructiveOnly = true;

        DryRunFileRow row = Assert.Single(viewModel.ProcessFiles);
        Assert.EndsWith("clobber.txt", row.SourcePath);
        Assert.Empty(viewModel.FilterSkips);
        Assert.Empty(viewModel.UnchangedSkips);

        viewModel.DestructiveOnly = false;
        Assert.Equal(2, viewModel.ProcessFiles.Count);
    }

    [Fact]
    public async Task Gateway_error_surfaces_as_a_banner()
    {
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = new IpcError("DRY_RUN_FAILED", "scan failed: boom");

        await viewModel.RunAsync(CancellationToken.None);

        Assert.False(viewModel.HasReport);
        Assert.Contains("boom", viewModel.ErrorMessage);
    }

    [Fact]
    public async Task Truncated_report_surfaces_a_notice_and_a_full_report_clears_it()
    {
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = SampleReport(viewModel.ProfileId!.Value) with { Truncated = true };

        await viewModel.RunAsync(CancellationToken.None);

        Assert.True(viewModel.WasTruncated);
        Assert.Contains("4", viewModel.TruncationNotice);
        Assert.Contains("truncated", viewModel.TruncationNotice, StringComparison.OrdinalIgnoreCase);

        gateway.DryRunResult = SampleReport(viewModel.ProfileId!.Value);
        await viewModel.RunAsync(CancellationToken.None);

        Assert.False(viewModel.WasTruncated);
        Assert.Equal("", viewModel.TruncationNotice);
    }

    [Fact]
    public async Task Transport_errors_point_at_the_service_log()
    {
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunResult = new IpcError("IPC_TRANSPORT", "connection closed");

        await viewModel.RunAsync(CancellationToken.None);

        Assert.False(viewModel.HasReport);
        Assert.Contains("connection closed", viewModel.ErrorMessage);
        Assert.Contains(@"FileManager\logs", viewModel.ErrorMessage);
    }

    [Fact]
    public async Task Unexpected_gateway_exception_surfaces_as_a_banner_not_a_fault()
    {
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunException = new InvalidOperationException("wire format drifted");

        await viewModel.RunAsync(CancellationToken.None);

        Assert.False(viewModel.HasReport);
        Assert.Contains("wire format drifted", viewModel.ErrorMessage);
    }

    [Fact]
    public async Task Scope_path_is_forwarded_trimmed_or_null()
    {
        var (viewModel, gateway) = NewViewModel();
        viewModel.ScopePath = "   ";
        await viewModel.RunAsync(CancellationToken.None);
        viewModel.ScopePath = @" C:\s\sub ";
        await viewModel.RunAsync(CancellationToken.None);

        Assert.Null(gateway.DryRunCalls[0].Scope);
        Assert.Equal(@"C:\s\sub", gateway.DryRunCalls[1].Scope);
    }

    [Fact]
    public async Task Cancellation_resets_to_a_calm_state()
    {
        var (viewModel, gateway) = NewViewModel();
        gateway.DryRunGate = new TaskCompletionSource();
        using CancellationTokenSource cts = new();

        Task running = viewModel.RunAsync(cts.Token);
        cts.Cancel();
        await running;

        Assert.False(viewModel.HasReport);
        Assert.Contains("cancelled", viewModel.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }
}
