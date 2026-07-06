using FileManager.Core.Jobs;
using System;

namespace FileManager.Core.Locking;

public sealed class SelfWriteSuppressionRegistry(TimeProvider time)
{
    public SuppressionToken Register(NormalizedPath path, JobId owner) =>
        throw new NotImplementedException();

    /// <summary>True while the registration is active OR within its post-release linger window.</summary>
    public bool IsSuppressed(NormalizedPath path) => throw new NotImplementedException();
}

public sealed class SuppressionToken : IDisposable
{
    public NormalizedPath Path { get; }

    public void Release(TimeSpan lingerWindow) => throw new NotImplementedException();

    void IDisposable.Dispose() => throw new NotImplementedException();   // Release with the default linger
}
