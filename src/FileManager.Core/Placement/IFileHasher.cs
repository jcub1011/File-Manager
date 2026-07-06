using FileManager.Contracts.Primitives;
using FileManager.Core.Jobs;
using System.Threading;
using System.Threading.Tasks;

namespace FileManager.Core.Placement;

public interface IFileHasher
{
    Task<Result<string, JobError>> HashFileAsync(string path, CancellationToken ct = default);
}
