using FileManager.Contracts.Primitives;
using FileManager.Core.Jobs;
using System.Threading;
using System.Threading.Tasks;

namespace FileManager.Core.Transformers;

public interface ITransformerChainRunner
{
    /// <summary>Success: absolute path of the final artifact inside the workspace.
    /// For a profile with no transformers this returns the source path itself and creates no workspace.</summary>
    Task<Result<string, TransformFailure>> RunAsync(JobExecution execution, CancellationToken ct = default);
}

public sealed record TransformFailure(
    int FailedStep, string StepName, string Reason, ProcessResult? Process);
