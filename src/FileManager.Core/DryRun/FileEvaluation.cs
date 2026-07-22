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
/// (the normalized <see cref="DryRunFile"/>/<see cref="DryRunOperation"/> shape does).</para>
/// <para>Implements <see cref="IEvaluationView"/> so the streamed accumulator can consume it
/// interchangeably with the pooled carrier a spilled run reads back
/// (<see cref="PooledEvaluation"/>). The concrete members stay the immutable records — the batch path
/// still does <c>record with { }</c> and the on-disk snapshot still serializes this exact shape — and
/// the view is a covariant projection over them; <see cref="IEvaluationView.Recycle"/> is a no-op
/// because an original record is never pool-owned.</para></summary>
internal sealed record FileEvaluation(
    PhysicalFile SourceFile,
    VirtualFileOperation SourceOp,
    IReadOnlyList<PhysicalFile> DestinationFiles,
    IReadOnlyList<VirtualFileOperation> DestinationOps) : IEvaluationView
{
    IPhysicalFileView IEvaluationView.SourceFile => SourceFile;
    IFileOperationView IEvaluationView.SourceOp => SourceOp;
    IReadOnlyList<IPhysicalFileView> IEvaluationView.DestinationFiles => DestinationFiles;
    IReadOnlyList<IFileOperationView> IEvaluationView.DestinationOps => DestinationOps;
    void IEvaluationView.Recycle() { }
}
