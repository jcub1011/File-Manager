using FileManager.Contracts.Primitives;

namespace FileManager.Core.Platform;

public interface IAutostartRegistrar
{
    Result RegisterAutostart();        // idempotent
    Result UnregisterAutostart();
}
