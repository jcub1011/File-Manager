using FileManager.Contracts.Primitives;

namespace FileManager.Core.Transformers;

public sealed record TokenContext
{
    public required string SourceRootPath { get; init; }
    public required string StepInputPath { get; init; }
    public required string? StepOutputPath { get; init; }   // null for InPlace steps
}

public interface ITokenExpander
{
    /// <summary>Expands one argv template element. Unknown token or $output in an InPlace step = error.</summary>
    Result<string, string> ExpandElement(string templateElement, TokenContext context);
}
