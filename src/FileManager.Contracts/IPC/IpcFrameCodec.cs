using FileManager.Contracts.Primitives;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FileManager.Contracts.IPC;

public static class IpcFrameCodec
{
    public static Task WriteFrameAsync(Stream stream, ReadOnlyMemory<byte> payload, CancellationToken ct = default) =>
        throw new NotImplementedException();

    public static Task<Result<byte[], string>> ReadFrameAsync(Stream stream, CancellationToken ct = default) =>
        throw new NotImplementedException();
}
