using System.Text.Json.Serialization;

namespace FileManager.Core.DryRun;

/// <summary>Source-generated (AOT-safe, no reflection) JSON for the on-disk dry-run snapshot records
/// the <see cref="FileDryRunSpool"/> writes. The snapshot is an internal, same-machine, same-version
/// file — never a wire contract — so it serializes the engine's <see cref="FileEvaluation"/> shape
/// (absolute-path <c>PhysicalFile</c>/<c>VirtualFileOperation</c>) directly rather than the normalized
/// wire shape.</summary>
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Default)]
[JsonSerializable(typeof(FileEvaluation))]
internal sealed partial class DryRunSnapshotJsonContext : JsonSerializerContext;
