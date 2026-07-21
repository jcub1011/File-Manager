using FileManager.Contracts.DryRun;
using System.Collections.Generic;

namespace FileManager.Core.DryRun;

/// <summary>One source file's contribution to the report: the source file node, its source-side
/// operation (Processed / Skipped + disposition), and the destination files/operations its targets
/// produce. Indices are bundle-local: a destination op's <c>SourceIndex == 0</c> means "this
/// bundle's source file" (else <c>-1</c>), and its <c>SubjectIndex</c> is a 0-based position into
/// this bundle's <see cref="DestinationFiles"/> (else <c>-1</c>). The report builders remap these
/// to global positions on append.
/// <para>Extracted from <see cref="DryRunEngine"/> so the dry-run spool (<see cref="IDryRunSpool"/>)
/// can persist and replay it; it is the engine's evaluation currency and never crosses the IPC wire
/// (the normalized <see cref="DryRunFile"/>/<see cref="DryRunOperation"/> shape does).</para></summary>
internal sealed record FileEvaluation(
    PhysicalFile SourceFile,
    VirtualFileOperation SourceOp,
    IReadOnlyList<PhysicalFile> DestinationFiles,
    IReadOnlyList<VirtualFileOperation> DestinationOps);
