using FileManager.Contracts.Primitives;
using FileManager.Platform.Windows;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileManager.Platform.Windows.Tests;

public sealed class WindowsTrashServiceTests
{
    private readonly WindowsTrashService _trash = new(NullLogger<WindowsTrashService>.Instance);

    [Fact]
    public void MoveToTrash_sends_a_real_file_to_the_recycle_bin()
    {
        // A real Recycle Bin operation on a throwaway temp file — verifies the IFileOperation COM
        // plumbing actually removes the file from its original location.
        string path = Path.Combine(Path.GetTempPath(), "fm-trash-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(path, "recycle me");

        try
        {
            Result result = _trash.MoveToTrash(path);
            Assert.True(result.IsSuccess, result.TryGetError(out string? err) ? err : "trash failed");
            Assert.False(File.Exists(path));   // gone from its original location
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void MoveToTrash_on_a_missing_path_returns_a_failure_not_a_throw()
    {
        if (!OperatingSystem.IsWindows())
            return;   // WindowsTrashService is Windows-only (shell COM); this assembly runs on Windows.

        string path = Path.Combine(Path.GetTempPath(), "fm-trash-missing-" + Guid.NewGuid().ToString("N") + ".txt");
        Assert.False(File.Exists(path));

        // The shell item cannot be resolved for a non-existent path; the COM plumbing must degrade to
        // a Result failure rather than throwing out of the disposition path.
        Result result = _trash.MoveToTrash(path);

        Assert.True(result.IsFailure);
    }

    // A "volume without a Recycle Bin" (some network shares / FAT removable media reject recycling)
    // cannot be reliably provisioned in a unit test, so that failure mode is intentionally not covered.
}
