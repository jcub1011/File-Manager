using FileManager.Contracts.Primitives;

namespace FileManager.Core.Platform;

public interface ITrashService
{
    Result MoveToTrash(string absolutePath);
}
