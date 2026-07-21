using Microsoft.Extensions.Logging;
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace FileManager.Core.DryRun;

/// <summary>Disk-backed dry-run spool with an in-memory fast path. Evaluation workers post findings to
/// a bounded channel; a single writer task drains it (keeping I/O off the worker threads and giving
/// lock-free ownership of the buffer/spill state). Findings stay in a memory list until their
/// estimated size crosses the spill threshold, at which point the buffered set — and every later
/// finding — is written append-only to a per-run <c>dryrun-*.snapshot</c> file. So a small run (the
/// common edit-preview) never touches disk, while a large run keeps the whole evaluated set off the
/// managed heap and reads back from a stable, immutable snapshot decoupled from UI consumption speed.
/// Framing is a 4-byte little-endian length prefix per record (like the IPC codec); write buffers are
/// reused and read buffers come from <see cref="ArrayPool{T}"/>, so the round-trip adds no per-record
/// array churn. The backing file is deleted on <see cref="DisposeAsync"/>.</summary>
internal sealed class FileDryRunSpool : IDryRunSpool
{
    private const int ChannelCapacity = 1024;
    private const int FileBufferSize = 64 * 1024;

    private readonly string _scratchDirectory;
    private readonly long _spillThresholdBytes;
    private readonly EvaluationCarrierPool _pool;
    private readonly ILogger _logger;

    private readonly Channel<FileEvaluation> _channel = Channel.CreateBounded<FileEvaluation>(
        new BoundedChannelOptions(ChannelCapacity) { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
    private readonly Task _writerTask;

    // Written by the writer task, read after it is joined (CompleteWritingAsync / DisposeAsync await
    // it, and that join is the memory fence — no lock needed on read-back).
    private List<FileEvaluation>? _buffered = [];
    private bool _spilled;
    private string? _filePath;
    private Exception? _writerError;

    public FileDryRunSpool(string scratchDirectory, long spillThresholdBytes, EvaluationCarrierPool pool, ILogger logger)
    {
        _scratchDirectory = scratchDirectory;
        _spillThresholdBytes = spillThresholdBytes;
        _pool = pool;
        _logger = logger;
        _writerTask = Task.Run(DrainAsync);
    }

    public ValueTask WriteAsync(FileEvaluation evaluation, CancellationToken ct) =>
        _channel.Writer.WriteAsync(evaluation, ct);

    public async ValueTask CompleteWritingAsync()
    {
        _channel.Writer.TryComplete();
        await _writerTask.ConfigureAwait(false);
        if (_writerError is not null)
            throw new IOException($"writing the dry-run snapshot failed: {_writerError.Message}", _writerError);
    }

    public async IAsyncEnumerable<IEvaluationView> ReadAllAsync([EnumeratorCancellation] CancellationToken ct)
    {
        if (!_spilled)
        {
            // Below threshold: the original records were never serialized, so replay them directly —
            // single-allocation, shared, not pool-owned (their Recycle is a no-op).
            foreach (FileEvaluation entry in _buffered ?? [])
            {
                ct.ThrowIfCancellationRequested();
                yield return entry;
            }
            yield break;
        }

        // Spilled: every record lives on disk. Read each back into rented carriers (parsed straight
        // from the framed bytes) instead of a fresh record graph — the allocation the snapshot
        // round-trip would otherwise reintroduce per file. The engine recycles each chunk's carriers
        // once the chunk is consumed, so the pool churns a bounded working set across the whole replay.
        await using FileStream file = new(
            _filePath!, FileMode.Open, FileAccess.Read, FileShare.Read, FileBufferSize, useAsync: true);
        byte[] lengthBuffer = new byte[4];
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            int read = await file.ReadAtLeastAsync(lengthBuffer, 4, throwOnEndOfStream: false, ct).ConfigureAwait(false);
            if (read == 0)
                yield break;      // clean end of stream
            if (read < 4)
                throw new IOException("the dry-run snapshot ended mid-record (truncated length prefix)");
            int length = BinaryPrimitives.ReadInt32LittleEndian(lengthBuffer);
            if (length < 0)
                throw new IOException($"the dry-run snapshot has a negative record length ({length})");

            PooledEvaluation entry;
            byte[] rented = ArrayPool<byte>.Shared.Rent(length);
            try
            {
                await file.ReadExactlyAsync(rented.AsMemory(0, length), ct).ConfigureAwait(false);
                entry = DryRunSnapshotFormat.Read(rented.AsSpan(0, length), _pool);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
            yield return entry;
        }
    }

    private async Task DrainAsync()
    {
        // Reused across every record so serialization allocates no per-record buffer (the writer is
        // single-threaded, so one writer/buffer pair is safe).
        ArrayBufferWriter<byte> jsonBuffer = new();
        Utf8JsonWriter jsonWriter = new(jsonBuffer);
        byte[] lengthBuffer = new byte[4];
        FileStream? file = null;
        long bufferedBytes = 0;
        try
        {
            await foreach (FileEvaluation entry in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                if (!_spilled)
                {
                    _buffered!.Add(entry);
                    bufferedBytes += EstimateBytes(entry);
                    if (bufferedBytes < _spillThresholdBytes)
                        continue;
                    // Cross the threshold: open the file, flush everything buffered so far, and switch
                    // to append-only mode. The memory list is released so the heap stays flat.
                    file = OpenSnapshotFile();
                    foreach (FileEvaluation buffered in _buffered!)
                        await WriteRecordAsync(file, buffered, jsonBuffer, jsonWriter, lengthBuffer).ConfigureAwait(false);
                    _buffered = null;
                    _spilled = true;
                }
                else
                {
                    await WriteRecordAsync(file!, entry, jsonBuffer, jsonWriter, lengthBuffer).ConfigureAwait(false);
                }
            }
            if (file is not null)
                await file.FlushAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Surfaced from CompleteWritingAsync; the channel drains so writers do not deadlock.
            _writerError = ex;
        }
        finally
        {
            jsonWriter.Dispose();
            if (file is not null)
                await file.DisposeAsync().ConfigureAwait(false);
        }
    }

    private FileStream OpenSnapshotFile()
    {
        Directory.CreateDirectory(_scratchDirectory);
        _filePath = Path.Combine(_scratchDirectory, $"dryrun-{Guid.NewGuid():N}.snapshot");
        return new FileStream(
            _filePath, FileMode.Create, FileAccess.Write, FileShare.None, FileBufferSize, useAsync: true);
    }

    private static async Task WriteRecordAsync(
        FileStream file, FileEvaluation entry,
        ArrayBufferWriter<byte> jsonBuffer, Utf8JsonWriter jsonWriter, byte[] lengthBuffer)
    {
        jsonBuffer.Clear();
        jsonWriter.Reset(jsonBuffer);
        DryRunSnapshotFormat.Write(jsonWriter, entry);
        jsonWriter.Flush();
        BinaryPrimitives.WriteInt32LittleEndian(lengthBuffer, jsonBuffer.WrittenCount);
        await file.WriteAsync(lengthBuffer).ConfigureAwait(false);
        await file.WriteAsync(jsonBuffer.WrittenMemory).ConfigureAwait(false);
    }

    /// <summary>A cheap over-estimate of a record's serialized size, used only to decide when to spill
    /// (an overestimate spills marginally early, never late). Mirrors the shape of the engine's
    /// upper-bound accounting without depending on it.</summary>
    private static long EstimateBytes(FileEvaluation entry)
    {
        long bytes = FileBytes(entry.SourceFile) + OpBytes(entry.SourceOp);
        foreach (var file in entry.DestinationFiles)
            bytes += FileBytes(file);
        foreach (var op in entry.DestinationOps)
            bytes += OpBytes(op);
        return bytes;

        static long FileBytes(Contracts.DryRun.PhysicalFile f) => 96 + f.Path.Length + f.Root.Length;
        static long OpBytes(Contracts.DryRun.VirtualFileOperation o) =>
            128 + o.Path.Length + o.Root.Length + (o.Detail?.Length ?? 0);
    }

    public async ValueTask DisposeAsync()
    {
        // Ensure the writer task has ended (and closed the file) before we delete it — covers the
        // cancel-mid-run path where CompleteWritingAsync was never called.
        _channel.Writer.TryComplete();
        try { await _writerTask.ConfigureAwait(false); }
        catch { /* a writer fault is reported via CompleteWritingAsync; disposal must not throw */ }

        if (_filePath is not null)
        {
            try { File.Delete(_filePath); }
            catch (Exception ex)
            {
                // A leftover snapshot is purged on the next service startup; a delete race is not fatal.
                _logger.LogWarning("Could not delete dry-run snapshot {Path}: {Error}", _filePath, ex.Message);
            }
        }
    }
}

/// <summary>Creates <see cref="FileDryRunSpool"/>s, resolving the scratch directory fresh per run so a
/// changed setting takes effect on the next dry run.</summary>
internal sealed class FileDryRunSpoolFactory(
    Func<string> scratchDirectoryProvider, long spillThresholdBytes, ILogger logger) : IDryRunSpoolFactory
{
    public IDryRunSpool Create() =>
        new FileDryRunSpool(scratchDirectoryProvider(), spillThresholdBytes, new EvaluationCarrierPool(), logger);
}
