using System.Buffers;
using FileManager.Contracts.DryRun;
using FileManager.Contracts.IPC;
using FileManager.Contracts.Primitives;
using FileManager.UI.ViewModels;
using Xunit.Abstractions;

namespace FileManager.UI.Tests;

/// <summary>Splits the client side of a streamed dry run into its two allocation phases — decoding a
/// wire frame, and folding the decoded records into the columnar store — and reports bytes per wire
/// record for each.
///
/// <para><strong>Why this exists.</strong> On the published exe a run allocates <strong>605 MB</strong>
/// in the UI process for 33,449 source files / 330,797 destination operations, against the
/// <em>service</em>'s 292 MB to produce and serialize the same run. `PrepareReport` accounts for well
/// under half of it at that scale, so the majority was known only as "the rest". The two candidates have
/// unrelated fixes — <c>IpcFrameCodec.ReadFrameAsync</c> allocating a fresh <c>byte[length]</c> per frame
/// plus <c>IpcSerializer.DeserializeResponse</c> materializing every record and string (pooling, and a
/// <c>Utf8JsonReader</c>-based fold), versus <c>DryRunRowStore.OnChunk</c>'s folding — so the ratio is
/// what decides which is worth touching. The app now logs this same split per run
/// (<c>DryRunIngestMeter</c>); this test gets the number without a service, and becomes the regression
/// gate if the decode path is changed.</para>
///
/// <para>Reported, not budgeted: these are absolute allocation figures for a decision, and the only
/// assertion is the one that would mean the test measures nothing.</para></summary>
[Collection("Memory")]
public sealed class DryRunStreamAllocationTests(ITestOutputHelper output)
{
    /// <summary>Records per chunk frame. The engine sizes chunks to a wire budget rather than a record
    /// count, so this is a stand-in for "one realistic frame" — the per-record figures are what carry
    /// over, not the per-frame ones.</summary>
    private const int RecordsPerChunk = 20_000;

    /// <param name="fileCount">Two scales, because the ratio between them separates a per-record cost
    /// (constant B/record) from a whole-payload cost such as polymorphic buffering (which would grow
    /// B/record with the frame). Which one it is decides whether a reader-based fold can capture it.</param>
    [Theory]
    [InlineData(10_000)]
    [InlineData(20_000)]
    [Trait("Category", "Memory")]
    public void Decoding_a_chunk_frame_costs_more_than_folding_it(int fileCount)
    {
        DryRunChunkResponse chunk = BuildChunk(fileCount);
        byte[] frame = IpcSerializer.SerializeResponse(chunk);
        long records = chunk.DirectoryName.Count + chunk.SourceFiles.Count
            + chunk.DestinationFiles.Count + chunk.SourceOperations.Count + chunk.DestinationOperations.Count;

        // Warm both paths first: the source-generated JSON contract and the store's first segments would
        // otherwise be charged to the measured pass as one-off costs.
        IpcSerializer.DeserializeResponse(frame).TryGetValue(out IpcResponse? warm);
        FoldOnce((DryRunChunkResponse)warm!);

        long deserializeBefore = GC.GetTotalAllocatedBytes(precise: true);
        Result<IpcResponse, string> parsed = IpcSerializer.DeserializeResponse(frame);
        long deserializeBytes = GC.GetTotalAllocatedBytes(precise: true) - deserializeBefore;
        Assert.True(parsed.TryGetValue(out IpcResponse? response), "the frame must round-trip, or nothing below is measuring the real path");
        DryRunChunkResponse decoded = Assert.IsType<DryRunChunkResponse>(response);

        long foldBefore = GC.GetTotalAllocatedBytes(precise: true);
        FoldOnce(decoded);
        long foldBytes = GC.GetTotalAllocatedBytes(precise: true) - foldBefore;

        output.WriteLine(
            $"One {frame.Length / (1024.0 * 1024.0):F1} MB chunk frame, {records:N0} wire records: " +
            $"frame buffer {frame.Length / (1024.0 * 1024.0):F1} MB + " +
            $"deserialize {deserializeBytes / (1024.0 * 1024.0):F1} MB ({(double)deserializeBytes / records:F0} B/record) " +
            $"vs fold-into-store {foldBytes / (1024.0 * 1024.0):F1} MB ({(double)foldBytes / records:F0} B/record); " +
            $"decode is {(double)(deserializeBytes + frame.Length) / Math.Max(foldBytes, 1):F1}x the fold");

        Assert.True(deserializeBytes > 0 && foldBytes > 0,
            "both phases must allocate something measurable, or the probe is not exercising them");
    }

    /// <summary>Splits chunk deserialization into strings versus everything else (the columns, their
    /// growth, and the reader), so the next decision about the decode path is made against a measurement.
    ///
    /// <para><strong>Named for what it measures, not for what it concluded.</strong> Two earlier versions
    /// of this test asserted a conclusion in their name and both inverted: pre-columnar the split was
    /// ~15-20% strings against ~80% per-record object materialization, and making the wire columnar
    /// deleted most of that 80% — leaving strings the larger share of a much smaller total. The ratio
    /// moves every time the decode path changes, which is the point of measuring it rather than
    /// remembering it.</para>
    ///
    /// <para>What the current split means for the remaining option, a <c>Utf8JsonReader</c> fold straight
    /// into the store's columns: it would capture both halves — the non-string remainder (list growth and
    /// per-token dispatch) and most of the strings too, since a reader can hash a name's UTF-8 bytes and
    /// materialize a string only for names the store has not already interned.</para>
    ///
    /// <para>Method: deserialize the same chunk twice, the second time with every <c>FileName</c> emptied
    /// and every <c>Detail</c> nulled. System.Text.Json returns the interned <see cref="string.Empty"/>
    /// for an empty string token and nothing at all for a null, so the stripped pass pays for the objects,
    /// the lists and the reader while paying for no name or detail strings. The delta is the string cost.
    /// The directory table is left identical in both, so directory names cancel out and are excluded —
    /// they are ~1k of ~58k records.</para>
    ///
    /// <para>This exploits the fact that <c>DryRunFile</c>/<c>DryRunOperation</c> are deliberately mutable
    /// classes (the service pools them), so the two chunks can be made to differ in exactly one respect.</para></summary>
    [Fact]
    [Trait("Category", "Memory")]
    public void Chunk_deserialization_splits_between_strings_and_the_reader()
    {
        DryRunChunkResponse realistic = BuildChunk(RecordsPerChunk);
        byte[] realisticFrame = IpcSerializer.SerializeResponse(realistic);

        DryRunChunkResponse stripped = BuildChunk(RecordsPerChunk);
        long names = 0, details = 0;
        for (int i = 0; i < stripped.SourceFiles.Count; i++)
        {
            stripped.SourceFiles.FileName[i] = "";
            names++;
        }
        foreach (DryRunOperationColumns ops in new[] { stripped.SourceOperations, stripped.DestinationOperations })
        {
            for (int i = 0; i < ops.Count; i++)
            {
                ops.FileName[i] = "";
                if (ops.Detail[i] is not null)
                    details++;
                ops.Detail[i] = null;
                names++;
            }
        }
        byte[] strippedFrame = IpcSerializer.SerializeResponse(stripped);

        // Warm both shapes so the source-generated contract's one-off costs are not charged to either.
        IpcSerializer.DeserializeResponse(realisticFrame);
        IpcSerializer.DeserializeResponse(strippedFrame);

        long withStrings = Measure(realisticFrame, out IpcResponse? decoded);
        long withoutStrings = Measure(strippedFrame, out IpcResponse? strippedDecoded);
        long stringBytes = withStrings - withoutStrings;

        // Proves the premise: the stripped pass really does allocate no name strings.
        Assert.Same(string.Empty, ((DryRunChunkResponse)strippedDecoded!).SourceFiles.FileName[0]);
        Assert.NotEmpty(((DryRunChunkResponse)decoded!).SourceFiles.FileName[0]);

        output.WriteLine(
            $"Deserializing {RecordsPerChunk:N0} files ({names:N0} name strings, {details:N0} detail strings): " +
            $"{withStrings / (1024.0 * 1024.0):F1} MB with strings, {withoutStrings / (1024.0 * 1024.0):F1} MB without — " +
            $"strings are {stringBytes / (1024.0 * 1024.0):F1} MB ({100 * stringBytes / withStrings}% of deserialization), " +
            $"{(double)stringBytes / names:F0} B per name; objects+lists+reader are " +
            $"{withoutStrings / (1024.0 * 1024.0):F1} MB ({100 * withoutStrings / withStrings}%)");

        Assert.True(stringBytes > 0, "the stripped chunk did not deserialize cheaper — the probe is not isolating strings");
        Assert.True(withoutStrings > 0,
            "the stripped pass allocated nothing measurable — the split is not being measured");
    }

    private static long Measure(byte[] frame, out IpcResponse? decoded)
    {
        long before = GC.GetTotalAllocatedBytes(precise: true);
        Result<IpcResponse, string> parsed = IpcSerializer.DeserializeResponse(frame);
        long bytes = GC.GetTotalAllocatedBytes(precise: true) - before;
        Assert.True(parsed.TryGetValue(out decoded), "the frame must round-trip");
        return bytes;
    }

    /// <summary>The frame-buffer half of the decode cost, and the fix for it. Reading N multi-megabyte
    /// frames with the exact-size-array overload allocates one Large Object Heap array per frame; reading
    /// them into one reused <see cref="ArrayBufferWriter{T}"/> allocates that capacity once.
    ///
    /// <para>Also asserts the two paths decode to the same payload — the saving is worthless if the
    /// reused-buffer path can hand a parser a stale tail from the previous, longer frame, which is
    /// exactly what <c>Clear()</c> plus committing only the frame's own length prevents.</para></summary>
    [Fact]
    [Trait("Category", "Memory")]
    public async Task Reading_frames_into_a_reused_buffer_costs_one_buffer_not_one_per_frame()
    {
        // Descending sizes on purpose: each frame is shorter than the buffer already holds, so a stale
        // tail would survive into WrittenSpan if the length bookkeeping were wrong.
        byte[][] frames =
        [
            IpcSerializer.SerializeResponse(BuildChunk(4_000)),
            IpcSerializer.SerializeResponse(BuildChunk(2_000)),
            IpcSerializer.SerializeResponse(BuildChunk(1_000)),
        ];

        long pooledBytes = await MeasureReads(frames, pooled: true);
        long perFrameBytes = await MeasureReads(frames, pooled: false);

        output.WriteLine(
            $"Reading {frames.Length} frames of {string.Join("/", frames.Select(f => $"{f.Length / (1024.0 * 1024.0):F2}MB"))}: " +
            $"reused buffer {pooledBytes / (1024.0 * 1024.0):F2} MB vs one array per frame " +
            $"{perFrameBytes / (1024.0 * 1024.0):F2} MB — {perFrameBytes - pooledBytes:N0} bytes saved " +
            $"({(perFrameBytes == 0 ? 0 : 100 * (perFrameBytes - pooledBytes) / perFrameBytes)}%)");

        Assert.True(pooledBytes < perFrameBytes,
            $"the reused buffer allocated {pooledBytes:N0} bytes against the per-frame path's {perFrameBytes:N0} — no saving");
    }

    /// <summary>Reads every frame back off a pipe-like stream through one of the two overloads, returning
    /// the bytes allocated doing it. Each decoded payload is compared against the original so a wrong
    /// length or a stale tail fails here rather than becoming a malformed-frame bug in the field.</summary>
    private static async Task<long> MeasureReads(byte[][] frames, bool pooled)
    {
        using MemoryStream stream = new();
        foreach (byte[] frame in frames)
        {
            byte[] header = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(header, frame.Length);
            stream.Write(header);
            stream.Write(frame);
        }
        stream.Position = 0;

        ArrayBufferWriter<byte> buffer = new();
        // Warm: the first read grows the writer and JITs the path, which is a one-off, not per-frame.
        long before = GC.GetTotalAllocatedBytes(precise: true);
        foreach (byte[] expected in frames)
        {
            if (pooled)
            {
                buffer.Clear();
                Result<int, string> read = await IpcFrameCodec.ReadFrameAsync(stream, buffer);
                Assert.True(read.TryGetValue(out int length), "the pooled read failed");
                Assert.Equal(expected.Length, length);
                Assert.True(buffer.WrittenSpan.SequenceEqual(expected),
                    "the reused buffer's written span did not match the frame — stale tail or wrong length");
            }
            else
            {
                Result<byte[], string> read = await IpcFrameCodec.ReadFrameAsync(stream);
                Assert.True(read.TryGetValue(out byte[]? payload), "the per-frame read failed");
                Assert.True(payload!.AsSpan().SequenceEqual(expected), "the per-frame read did not match");
            }
        }
        return GC.GetTotalAllocatedBytes(precise: true) - before;
    }

    /// <summary>Folds one chunk into a fresh store, exactly as the stream pump does.</summary>
    private static void FoldOnce(DryRunChunkResponse chunk)
    {
        DryRunRowStore store = DryRunRowStore.CreateForIngest();
        store.OnChunk(chunk);
        GC.KeepAlive(store);
    }

    /// <summary>One chunk in the deep-path shape the memory probes use (~100-char paths, 26-char distinct
    /// names, 20 files per leaf directory), with the same two-of-three-files-produce-an-operation mix, so
    /// the per-record figures are comparable to the rest of the suite's numbers.</summary>
    internal static DryRunChunkResponse BuildChunk(int fileCount)
    {
        DryRunDirectoryTableBuilder dirs = new();
        List<DryRunFile> sourceFiles = new(fileCount);
        List<DryRunOperation> sourceOps = new(fileCount);
        List<DryRunOperation> destinationOps = [];

        for (int i = 0; i < fileCount; i++)
        {
            int leaf = i / 20;
            string sourceDir = $@"C:\media-archive\projects\project-{leaf % 500:D3}\assets\renders\batch-{leaf / 500:D4}";
            string source = $@"{sourceDir}\render-output-{i:D7}.png";
            sourceFiles.Add(dirs.Convert(new PhysicalFile
            {
                Path = source,
                Root = @"C:\media-archive\projects",
                Length = i,
                LastWritten = DateTimeOffset.UnixEpoch,
            }));
            sourceOps.Add(dirs.Convert(new VirtualFileOperation
            {
                Path = source,
                Root = @"C:\media-archive\projects",
                Kind = i % 3 == 1 ? OperationKind.SkippedByFilter : OperationKind.Processed,
                SourceIndex = i,
                Detail = i % 3 == 1 ? "exclude *.tmp" : null,
            }));
            if (i % 3 == 1)
                continue;
            destinationOps.Add(dirs.Convert(new VirtualFileOperation
            {
                Path = $@"D:\backup\media-archive\projects\project-{leaf % 500:D3}\assets\renders\batch-{leaf / 500:D4}\render-output-{i:D7}.png",
                Root = @"D:\backup\media-archive\projects",
                Kind = OperationKind.New,
                SourceIndex = i,
            }));
        }

        return DryRunColumns.ToChunk(
            dirs.Entries.ToList(), sourceFiles, sourceOperations: sourceOps, destinationOperations: destinationOps);
    }
}
