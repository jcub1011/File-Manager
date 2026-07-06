using FileManager.Contracts.Primitives;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FileManager.Core.Transformers;

public sealed record ProcessRequest
{
    public required string ExecutablePath { get; init; }
    public required IReadOnlyList<string> Arguments { get; init; }   // argv, no shell
    public required string WorkingDirectory { get; init; }
    public required TimeSpan Timeout { get; init; }
}

public sealed record ProcessResult(
    int ExitCode, bool TimedOut, string StdOut, string StdErr, TimeSpan Duration);

public interface IProcessRunner
{
    Task<Result<ProcessResult, string>> RunAsync(ProcessRequest request, CancellationToken ct = default);
}
