using FileManager.Contracts.Primitives;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FileManager.Core.Platform;

public interface IIpcEndpointProvider
{
    /// <summary>Server side: a listening stream for one connection cycle.</summary>
    Task<Result<Stream, string>> AcceptAsync(CancellationToken ct = default);
}
