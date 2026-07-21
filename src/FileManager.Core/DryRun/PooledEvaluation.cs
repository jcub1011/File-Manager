using FileManager.Contracts.DryRun;
using FileManager.Contracts.Profiles;
using System;
using System.Collections.Generic;
using System.Text.Json;

namespace FileManager.Core.DryRun;

/// <summary>The read-back currency of a <em>spilled</em> dry-run spool: a pool-owned, mutable stand-in
/// for the immutable <see cref="FileEvaluation"/>/<see cref="PhysicalFile"/>/<see cref="VirtualFileOperation"/>
/// records that a naïve replay would allocate fresh for every file. On a large run that spills to disk
/// (hundreds of thousands of files) this second record set is the allocation the snapshot round-trip
/// introduced; renting carriers instead and recycling them per chunk removes it (I-POOL-RECYCLE, see
/// <see cref="DryRunChunk"/>). Small runs never spill, so they keep replaying the original records and
/// never touch a carrier.
///
/// <para>Carriers implement the read-only <see cref="IPhysicalFileView"/>/<see cref="IFileOperationView"/>
/// views so a <see cref="DryRunChunk"/> can hold them interchangeably with the concrete records; only
/// the engine's streamed accumulator, holding the concrete carrier type, mutates their index fields for
/// the global remap. Plain classes (no reflection, no unmanaged state) — AOT-safe.</para></summary>
internal sealed class PooledPhysicalFile : IPhysicalFileView
{
    public string Path { get; set; } = "";
    public string Root { get; set; } = "";
    public long Length { get; set; }
    public DateTimeOffset LastWritten { get; set; }
    public bool IsReparsePoint { get; set; }
}

/// <summary>Mutable carrier for a <see cref="VirtualFileOperation"/> on the spool read-back path — the
/// operation analogue of <see cref="PooledPhysicalFile"/>. Its <see cref="SourceIndex"/>/
/// <see cref="SubjectIndex"/> are rewritten in place by the streamed accumulator's global-index remap,
/// which is exactly why the streamed chunk is backed by carriers rather than <c>record with { }</c>
/// copies.</summary>
internal sealed class PooledFileOperation : IFileOperationView
{
    public string Path { get; set; } = "";
    public string Root { get; set; } = "";
    public OperationKind Kind { get; set; }
    public int SourceIndex { get; set; } = -1;
    public int SubjectIndex { get; set; } = -1;
    public OnSuccessAction? SourceDisposition { get; set; }
    public string? Detail { get; set; }
}

/// <summary>Marker for a spool entry the streamed accumulator can consume: either an original
/// <see cref="FileEvaluation"/> record (in-memory / non-spilled replay — <see cref="Recycle"/> is a
/// no-op) or a <see cref="PooledEvaluation"/> carrier (spilled replay — <see cref="Recycle"/> returns
/// its carriers to the pool). The view accessors let the accumulator size and append an entry without
/// knowing which it holds; the index remap switches on the concrete type because only the carrier is
/// mutable.</summary>
internal interface IEvaluationView
{
    IPhysicalFileView SourceFile { get; }
    IFileOperationView SourceOp { get; }
    IReadOnlyList<IPhysicalFileView> DestinationFiles { get; }
    IReadOnlyList<IFileOperationView> DestinationOps { get; }

    /// <summary>Returns any pool-owned carriers this entry holds to their pool. A no-op for original
    /// records. Called by the engine only after the chunk carrying this entry has been fully consumed
    /// (its <c>yield return</c> resumed) — never while a consumer might still read it.</summary>
    void Recycle();
}

/// <summary>One pooled file evaluation: the same bipartite shape as <see cref="FileEvaluation"/> but
/// built from rented carriers. Owns its child lists (also pooled), so <see cref="Recycle"/> hands the
/// whole graph back in one call.</summary>
internal sealed class PooledEvaluation : IEvaluationView
{
    private readonly EvaluationCarrierPool _pool;

    public PooledEvaluation(EvaluationCarrierPool pool) => _pool = pool;

    public PooledPhysicalFile SourceFile = null!;
    public PooledFileOperation SourceOp = null!;
    public List<PooledPhysicalFile> DestinationFiles = null!;
    public List<PooledFileOperation> DestinationOps = null!;

    IPhysicalFileView IEvaluationView.SourceFile => SourceFile;
    IFileOperationView IEvaluationView.SourceOp => SourceOp;
    IReadOnlyList<IPhysicalFileView> IEvaluationView.DestinationFiles => DestinationFiles;
    IReadOnlyList<IFileOperationView> IEvaluationView.DestinationOps => DestinationOps;

    public void Recycle() => _pool.Return(this);

    /// <summary>Parses one snapshot record — the exact JSON <see cref="DryRunSnapshotJsonContext"/>
    /// writes for a <see cref="FileEvaluation"/> — into a rented carrier graph, reading straight from
    /// the framed bytes with a <see cref="Utf8JsonReader"/> so nothing intermediate is allocated. Enum
    /// members are numbers (the source generator's default) and properties may arrive in any order;
    /// unknown properties are skipped, so an additive change to the record shape does not break the
    /// read even before the writer is regenerated. If the on-disk record shape changes materially,
    /// update both this parser and the write path.</summary>
    public static PooledEvaluation Parse(ReadOnlySpan<byte> json, EvaluationCarrierPool pool)
    {
        PooledEvaluation e = pool.RentEvaluation();
        Utf8JsonReader reader = new(json);

        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("dry-run snapshot record is not a JSON object");

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("SourceFile"u8))
            {
                reader.Read();
                ReadFile(ref reader, e.SourceFile);
            }
            else if (reader.ValueTextEquals("SourceOp"u8))
            {
                reader.Read();
                ReadOp(ref reader, e.SourceOp);
            }
            else if (reader.ValueTextEquals("DestinationFiles"u8))
            {
                reader.Read();   // StartArray
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    PooledPhysicalFile f = pool.RentFile();
                    ReadFile(ref reader, f);
                    e.DestinationFiles.Add(f);
                }
            }
            else if (reader.ValueTextEquals("DestinationOps"u8))
            {
                reader.Read();   // StartArray
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    PooledFileOperation o = pool.RentOp();
                    ReadOp(ref reader, o);
                    e.DestinationOps.Add(o);
                }
            }
            else
            {
                reader.Read();
                reader.Skip();
            }
        }
        return e;
    }

    // Each reader is positioned on the value's StartObject on entry and left on the matching EndObject,
    // so the caller's next Read() advances to the following property / array element.
    private static void ReadFile(ref Utf8JsonReader reader, PooledPhysicalFile f)
    {
        f.Length = 0;
        f.IsReparsePoint = false;
        f.LastWritten = default;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("Path"u8)) { reader.Read(); f.Path = reader.GetString() ?? ""; }
            else if (reader.ValueTextEquals("Root"u8)) { reader.Read(); f.Root = reader.GetString() ?? ""; }
            else if (reader.ValueTextEquals("Length"u8)) { reader.Read(); f.Length = reader.GetInt64(); }
            else if (reader.ValueTextEquals("LastWritten"u8)) { reader.Read(); f.LastWritten = reader.GetDateTimeOffset(); }
            else if (reader.ValueTextEquals("IsReparsePoint"u8)) { reader.Read(); f.IsReparsePoint = reader.GetBoolean(); }
            else { reader.Read(); reader.Skip(); }
        }
    }

    private static void ReadOp(ref Utf8JsonReader reader, PooledFileOperation o)
    {
        o.SourceIndex = -1;
        o.SubjectIndex = -1;
        o.SourceDisposition = null;
        o.Detail = null;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("Path"u8)) { reader.Read(); o.Path = reader.GetString() ?? ""; }
            else if (reader.ValueTextEquals("Root"u8)) { reader.Read(); o.Root = reader.GetString() ?? ""; }
            else if (reader.ValueTextEquals("Kind"u8)) { reader.Read(); o.Kind = (OperationKind)reader.GetInt32(); }
            else if (reader.ValueTextEquals("SourceIndex"u8)) { reader.Read(); o.SourceIndex = reader.GetInt32(); }
            else if (reader.ValueTextEquals("SubjectIndex"u8)) { reader.Read(); o.SubjectIndex = reader.GetInt32(); }
            else if (reader.ValueTextEquals("SourceDisposition"u8))
            {
                reader.Read();
                o.SourceDisposition = reader.TokenType == JsonTokenType.Null ? null : (OnSuccessAction)reader.GetInt32();
            }
            else if (reader.ValueTextEquals("Detail"u8))
            {
                reader.Read();
                o.Detail = reader.TokenType == JsonTokenType.Null ? null : reader.GetString();
            }
            else { reader.Read(); reader.Skip(); }
        }
    }
}

/// <summary>A bounded, per-run pool of the carriers a spilled dry-run spool reads back into. Created
/// fresh in <c>DryRunEngine.SimulateStreamAsync</c> and shared with the spool (rents on read) and the
/// engine (recycles each chunk once it is consumed), so its lifetime is one dry run and it is dropped
/// with the run. <b>Not thread-safe by design:</b> the streamed read-back is a single sequential async
/// flow (the evaluation write phase has fully completed and been joined before any read), so renting
/// and recycling never race and no lock is paid. Retained counts are capped so idle memory stays flat
/// regardless of file count; recycling past a cap simply drops the carrier for the GC (correctness is
/// unaffected — over-recycling never hands the same instance out twice).</summary>
internal sealed class EvaluationCarrierPool
{
    // Comfortably above one ~1 MiB chunk's carrier count (a chunk's cheap upper-bound sizing puts a
    // few thousand records in a chunk), so a full chunk recycles without dropping, yet idle memory
    // stays a small constant rather than growing with the run.
    private const int MaxRetainedPerKind = 16_384;

    private readonly Stack<PooledPhysicalFile> _files = new();
    private readonly Stack<PooledFileOperation> _ops = new();
    private readonly Stack<PooledEvaluation> _evaluations = new();
    private readonly Stack<List<PooledPhysicalFile>> _fileLists = new();
    private readonly Stack<List<PooledFileOperation>> _opLists = new();

    /// <summary>Evaluations handed out over the pool's life. Paired with <see cref="ReturnedEvaluations"/>
    /// it proves every rented entry was recycled (tests assert equality after a spilled run).</summary>
    public long RentedEvaluations { get; private set; }
    public long ReturnedEvaluations { get; private set; }

    /// <summary>Total carriers/lists currently held for reuse — bounded (not proportional to file
    /// count), the memory-flatness property tests assert.</summary>
    public int RetainedCount =>
        _files.Count + _ops.Count + _evaluations.Count + _fileLists.Count + _opLists.Count;

    public PooledPhysicalFile RentFile() => _files.Count > 0 ? _files.Pop() : new PooledPhysicalFile();

    public PooledFileOperation RentOp() => _ops.Count > 0 ? _ops.Pop() : new PooledFileOperation();

    public PooledEvaluation RentEvaluation()
    {
        RentedEvaluations++;
        PooledEvaluation e = _evaluations.Count > 0 ? _evaluations.Pop() : new PooledEvaluation(this);
        e.SourceFile = RentFile();
        e.SourceOp = RentOp();
        e.DestinationFiles = _fileLists.Count > 0 ? _fileLists.Pop() : [];
        e.DestinationOps = _opLists.Count > 0 ? _opLists.Pop() : [];
        return e;
    }

    /// <summary>Returns an evaluation and every carrier/list it owns to the pool. Idempotency is the
    /// caller's contract (the engine recycles each chunk's entries exactly once, after the chunk is
    /// consumed); this does not guard against a double return.</summary>
    public void Return(PooledEvaluation e)
    {
        ReturnedEvaluations++;

        foreach (PooledPhysicalFile f in e.DestinationFiles)
            ReturnFile(f);
        foreach (PooledFileOperation o in e.DestinationOps)
            ReturnOp(o);
        ReturnFile(e.SourceFile);
        ReturnOp(e.SourceOp);

        e.DestinationFiles.Clear();
        e.DestinationOps.Clear();
        if (_fileLists.Count < MaxRetainedPerKind) _fileLists.Push(e.DestinationFiles);
        if (_opLists.Count < MaxRetainedPerKind) _opLists.Push(e.DestinationOps);

        e.SourceFile = null!;
        e.SourceOp = null!;
        e.DestinationFiles = null!;
        e.DestinationOps = null!;
        if (_evaluations.Count < MaxRetainedPerKind) _evaluations.Push(e);
    }

    private void ReturnFile(PooledPhysicalFile f)
    {
        if (_files.Count < MaxRetainedPerKind) _files.Push(f);
    }

    private void ReturnOp(PooledFileOperation o)
    {
        if (_ops.Count < MaxRetainedPerKind) _ops.Push(o);
    }
}
