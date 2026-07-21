using FileManager.Contracts.DryRun;
using FileManager.Contracts.Profiles;
using System;
using System.Collections.Generic;
using System.Text.Json;

namespace FileManager.Core.DryRun;

/// <summary>Per-run dedup of root strings on the spool read-back. A run has only a handful of distinct
/// roots (the profile's source/target roots) yet every file and op carries one, so materializing a
/// fresh string each time is pure waste. <see cref="Intern"/> matches the current JSON token against
/// the roots already seen with <see cref="System.Text.Json.Utf8JsonReader.ValueTextEquals(string)"/> —
/// a UTF-8 compare, no decode, no allocation — and only allocates on the first sighting of a new root.
/// The distinct set is tiny so the linear scan and the retained list are both trivially bounded. Not
/// thread-safe: the read-back is a single sequential flow (see <see cref="EvaluationCarrierPool"/>).</summary>
internal sealed class RootInterner
{
    private readonly List<string> _roots = [];

    /// <summary>The shared instance for the string token the reader is positioned on, allocating it
    /// only if this root has not been seen in this run.</summary>
    public string Intern(ref Utf8JsonReader reader)
    {
        for (int i = 0; i < _roots.Count; i++)
            if (reader.ValueTextEquals(_roots[i]))
                return _roots[i];
        string fresh = reader.GetString()!;
        _roots.Add(fresh);
        return fresh;
    }
}

/// <summary>The on-disk shape of a spilled dry-run finding, hand-written and hand-read so the format can
/// drop everything derivable and dedup everything repeated — the string-allocation levers a source-gen
/// round-trip cannot reach. The file is internal, same-machine, same-version, so the shape is ours to
/// shrink. Three reductions over the naïve one-record-per-file JSON:
/// <list type="bullet">
/// <item><b>Omit the source op's Path/Root</b> — always equal to the source file's (both are the
/// payload's source path/root), so the reader reuses the file's string references. Saves two
/// allocations and their bytes per finding.</item>
/// <item><b>Omit a destination op's Path when it targets a pre-existing file</b>
/// (<c>SubjectIndex &gt;= 0</c>): for Overwrite/SkipUnchanged/SkipConflict/Untouched the op's path is
/// exactly that subject file's path, so the reader reuses it. New/Rename ops (no subject) keep their
/// path. Saves the (long) path allocation for the common overwrite/skip cases.</item>
/// <item><b>Intern roots and omit default-valued fields</b> (false <c>IsReparsePoint</c>, null
/// <c>SourceDisposition</c>/<c>Detail</c>) — fewer bytes, and every root collapses to one string per
/// run via <see cref="RootInterner"/>.</item>
/// </list>
/// Enums are written as numbers. The writer only ever sees the immutable records (the spool serializes
/// original <see cref="FileEvaluation"/>s); the reader fills pooled carriers. Both live here so the two
/// halves of the format stay in lockstep — change one, change the other.</summary>
internal static class DryRunSnapshotFormat
{
    // ---- write (from the immutable records the spool holds) ----

    public static void Write(Utf8JsonWriter writer, FileEvaluation e)
    {
        writer.WriteStartObject();

        writer.WritePropertyName("SourceFile"u8);
        WriteFile(writer, e.SourceFile);

        // Source op: Path/Root omitted (== SourceFile's), reconstructed on read.
        writer.WritePropertyName("SourceOp"u8);
        WriteOp(writer, e.SourceOp, writePath: false, writeRoot: false);

        writer.WritePropertyName("DestinationFiles"u8);
        writer.WriteStartArray();
        foreach (PhysicalFile f in e.DestinationFiles)
            WriteFile(writer, f);
        writer.WriteEndArray();

        // Dest ops: Path omitted when it targets a subject file (== that file's path).
        writer.WritePropertyName("DestinationOps"u8);
        writer.WriteStartArray();
        foreach (VirtualFileOperation o in e.DestinationOps)
            WriteOp(writer, o, writePath: o.SubjectIndex < 0, writeRoot: true);
        writer.WriteEndArray();

        writer.WriteEndObject();
    }

    private static void WriteFile(Utf8JsonWriter w, PhysicalFile f)
    {
        w.WriteStartObject();
        w.WriteString("Path"u8, f.Path);
        w.WriteString("Root"u8, f.Root);
        w.WriteNumber("Length"u8, f.Length);
        w.WriteString("LastWritten"u8, f.LastWritten);
        if (f.IsReparsePoint)
            w.WriteBoolean("IsReparsePoint"u8, true);   // omitted when false (the default)
        w.WriteEndObject();
    }

    private static void WriteOp(Utf8JsonWriter w, VirtualFileOperation o, bool writePath, bool writeRoot)
    {
        w.WriteStartObject();
        if (writePath)
            w.WriteString("Path"u8, o.Path);
        if (writeRoot)
            w.WriteString("Root"u8, o.Root);
        w.WriteNumber("Kind"u8, (int)o.Kind);
        w.WriteNumber("SourceIndex"u8, o.SourceIndex);
        w.WriteNumber("SubjectIndex"u8, o.SubjectIndex);
        if (o.SourceDisposition is { } disposition)
            w.WriteNumber("SourceDisposition"u8, (int)disposition);   // omitted when null
        if (o.Detail is { } detail)
            w.WriteString("Detail"u8, detail);                        // omitted when null
        w.WriteEndObject();
    }

    // ---- read (into pooled carriers) ----

    /// <summary>Reads one record into a rented carrier graph. Properties may arrive in any order and
    /// unknown ones are skipped (additive-change tolerant). Omitted fields are reconstructed after the
    /// object is fully read, so the reconstruction never depends on element order within the record.</summary>
    public static PooledEvaluation Read(ReadOnlySpan<byte> json, EvaluationCarrierPool pool)
    {
        PooledEvaluation e = pool.RentEvaluation();
        RootInterner roots = pool.Roots;
        Utf8JsonReader reader = new(json);

        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("dry-run snapshot record is not a JSON object");

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("SourceFile"u8))
            {
                reader.Read();
                ReadFile(ref reader, e.SourceFile, roots);
            }
            else if (reader.ValueTextEquals("SourceOp"u8))
            {
                reader.Read();
                ReadOp(ref reader, e.SourceOp, roots);
            }
            else if (reader.ValueTextEquals("DestinationFiles"u8))
            {
                reader.Read();   // StartArray
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    PooledPhysicalFile f = pool.RentFile();
                    ReadFile(ref reader, f, roots);
                    e.DestinationFiles.Add(f);
                }
            }
            else if (reader.ValueTextEquals("DestinationOps"u8))
            {
                reader.Read();   // StartArray
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    PooledFileOperation o = pool.RentOp();
                    ReadOp(ref reader, o, roots);
                    e.DestinationOps.Add(o);
                }
            }
            else
            {
                reader.Read();
                reader.Skip();
            }
        }

        // Reconstruct the omitted fields (order-independent — done once the whole record is read).
        // Source op path/root are the source file's; a subject-targeting dest op's path is its subject
        // file's. Reusing those references is exactly optimizations B and (source-side) C.
        e.SourceOp.Path = e.SourceFile.Path;
        e.SourceOp.Root = e.SourceFile.Root;
        foreach (PooledFileOperation o in e.DestinationOps)
            if (o.Path is null)
                o.Path = e.DestinationFiles[o.SubjectIndex].Path;

        return e;
    }

    // Each reader is positioned on the value's StartObject on entry and left on the matching EndObject,
    // so the caller's next Read() advances to the following property / array element.
    private static void ReadFile(ref Utf8JsonReader reader, PooledPhysicalFile f, RootInterner roots)
    {
        f.Length = 0;
        f.IsReparsePoint = false;
        f.LastWritten = default;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("Path"u8)) { reader.Read(); f.Path = reader.GetString() ?? ""; }
            else if (reader.ValueTextEquals("Root"u8)) { reader.Read(); f.Root = roots.Intern(ref reader); }
            else if (reader.ValueTextEquals("Length"u8)) { reader.Read(); f.Length = reader.GetInt64(); }
            else if (reader.ValueTextEquals("LastWritten"u8)) { reader.Read(); f.LastWritten = reader.GetDateTimeOffset(); }
            else if (reader.ValueTextEquals("IsReparsePoint"u8)) { reader.Read(); f.IsReparsePoint = reader.GetBoolean(); }
            else { reader.Read(); reader.Skip(); }
        }
    }

    private static void ReadOp(ref Utf8JsonReader reader, PooledFileOperation o, RootInterner roots)
    {
        // Null Path is the sentinel for "omitted — reconstruct after the record is read".
        o.Path = null!;
        o.Root = null!;
        o.SourceIndex = -1;
        o.SubjectIndex = -1;
        o.SourceDisposition = null;
        o.Detail = null;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("Path"u8)) { reader.Read(); o.Path = reader.GetString() ?? ""; }
            else if (reader.ValueTextEquals("Root"u8)) { reader.Read(); o.Root = roots.Intern(ref reader); }
            else if (reader.ValueTextEquals("Kind"u8)) { reader.Read(); o.Kind = (OperationKind)reader.GetInt32(); }
            else if (reader.ValueTextEquals("SourceIndex"u8)) { reader.Read(); o.SourceIndex = reader.GetInt32(); }
            else if (reader.ValueTextEquals("SubjectIndex"u8)) { reader.Read(); o.SubjectIndex = reader.GetInt32(); }
            else if (reader.ValueTextEquals("SourceDisposition"u8)) { reader.Read(); o.SourceDisposition = (OnSuccessAction)reader.GetInt32(); }
            else if (reader.ValueTextEquals("Detail"u8)) { reader.Read(); o.Detail = reader.GetString(); }
            else { reader.Read(); reader.Skip(); }
        }
    }
}
