using FileManager.Contracts.Primitives;

namespace FileManager.Core.Platform;

public interface IShellIntegration
{
    Result RegisterContextMenu();      // idempotent; run at every service start
    Result UnregisterContextMenu();
}
