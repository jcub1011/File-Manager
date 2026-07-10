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
}
