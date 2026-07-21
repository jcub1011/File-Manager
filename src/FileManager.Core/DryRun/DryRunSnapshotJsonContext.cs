using System.Text.Json.Serialization;

namespace FileManager.Core.DryRun;

/// <summary>Source-generated (AOT-safe, no reflection) JSON for the full <see cref="FileEvaluation"/>
/// shape. The production spool no longer uses it — <see cref="DryRunSnapshotFormat"/> hand-writes and
/// hand-reads a trimmed shape that drops derivable/repeated fields for far fewer read-back allocations.
/// It is retained as the "naïve full-record round-trip" baseline the read-back benchmark measures the
/// trimmed format against.</summary>
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Default)]
[JsonSerializable(typeof(FileEvaluation))]
internal sealed partial class DryRunSnapshotJsonContext : JsonSerializerContext;
