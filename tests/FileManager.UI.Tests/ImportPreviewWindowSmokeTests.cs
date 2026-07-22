using FileManager.UI.ViewModels;
using FileManager.UI.Views;

namespace FileManager.UI.Tests;

/// <summary>Loads the real ImportPreviewWindow so runtime-only XAML failures (the StringFormat
/// bindings, the danger banners, the string-list templates) surface here, not in the app.</summary>
[Collection(HeadlessCollection.Name)]
public sealed class ImportPreviewWindowSmokeTests(HeadlessSessionFixture headless)
{
    [Fact]
    public async Task Import_preview_window_loads_with_a_destructive_transformer_profile()
    {
        await headless.Session.Dispatch(() =>
        {
            ImportPreviewWindow window = new()
            {
                DataContext = new ImportPreviewViewModel
                {
                    FileName = "alpha.json",
                    ProfileName = "Alpha",
                    Sources = [@"C:\src\music", @"C:\src\photos"],
                    Targets = [@"D:\library"],
                    DispositionText = "Source files are PERMANENTLY DELETED after delivery.",
                    IsDestructive = true,
                    VerificationText = "Verification: XxHash128",
                    SyncModeText = "Sync mode: AdditiveArchive",
                    TransformerLines = [@"Step 1 ""convert"" runs: C:\tools\conv.exe $input $output"],
                },
            };
            window.Show();
        }, CancellationToken.None);
    }
}
