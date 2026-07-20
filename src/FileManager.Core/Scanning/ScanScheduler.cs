using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using FileManager.Contracts.Primitives;
using FileManager.Contracts.Settings;
using FileManager.Core.Files;
using FileManager.Core.Settings;
using Microsoft.Extensions.Logging;

namespace FileManager.Core.Scanning;

/// <summary>The process-wide directory-scan scheduler (§4.2). Replaces the two hand-rolled
/// work-stealing walks (SourceScanner / DestinationProjector) with one long-lived owner of all
/// enumeration worker threads. Enforces a global thread ceiling (<c>MaxScanThreads</c>) and a
/// per-volume cap (specific → drive-type → default) that is a shared physical-device budget across
/// every open session.
///
/// Threads are LongRunning (off the ThreadPool, matching the former walks) and are spawned lazily up
/// to the current global cap as work arrives, then retire after an idle timeout — so an idle service
/// holds no scan threads. Workers pull directories round-robin across sessions and across each
/// session's volumes (fair-share, bounded wait → no starvation). A directory runs only while its
/// worker holds both a global thread and a slot in its volume's cap.
///
/// Backpressure is per-session and never blocks a shared worker: a worker that fills a session's
/// bounded output parks the current directory (its remaining entries and any un-written result),
/// releases its volume slot, and moves to other work; the consumer draining the buffer re-arms the
/// parked work. A slow consumer on one session therefore cannot stall workers serving another.</summary>
public sealed class ScanScheduler : IScanScheduler, IDisposable
{
    private const int IdleTimeoutMs = 15_000;

    private readonly ILogger<ScanScheduler> _logger;
    private readonly IFileSystemService _fileSystem;
    private readonly ISettingsProvider _settings;

    // Guards every mutable field below AND all per-session state; also the Monitor workers wait on.
    private readonly object _lock = new();
    private readonly List<ScanSession> _sessions = [];
    private int _sessionCursor;   // round-robin across sessions
    private readonly Dictionary<string, VolumeState> _volumes = new(StringComparer.Ordinal);   // keys pre-normalized
    private int _liveWorkers;
    private bool _shutdown;

    public ScanScheduler(ILogger<ScanScheduler> logger, IFileSystemService fileSystem, ISettingsProvider settings)
    {
        _logger = logger;
        _fileSystem = fileSystem;
        _settings = settings;
    }

    public IScanSession OpenSession(ScanSessionOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        ScanThreadingSettings threading = _settings.Current.ScanThreading;
        ScanSession session = new(this, options, threading, ct);
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_shutdown, this);
            _sessions.Add(session);
            // A new scan picks up the current settings: refresh known volume caps to this snapshot.
            foreach (VolumeState v in _volumes.Values)
                v.Cap = ScanThreadResolver.ResolvePerDriveCap(threading, v.Key, v.DriveClass);
        }
        return session;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _shutdown = true;
            Monitor.PulseAll(_lock);
        }
    }

    // ---- worker lifecycle -------------------------------------------------

    // Called under _lock after new work appears: spawn one more worker if we are below the current
    // global ceiling. Over successive submits this ramps up to the cap; idle workers retire back down.
    private void MaybeSpawnWorkerLocked()
    {
        if (_shutdown)
            return;
        int cap = Math.Max(1, ScanThreadResolver.ResolveMaxScanThreads(_settings.Current.ScanThreading));
        if (_liveWorkers >= cap)
            return;
        _liveWorkers++;
        Task.Factory.StartNew(WorkerLoop, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    private void WorkerLoop()
    {
        try
        {
            while (true)
            {
                ScanSession? session;
                ScanWorkState? work;
                lock (_lock)
                {
                    while (true)
                    {
                        if (_shutdown)
                        {
                            _liveWorkers--;
                            return;
                        }
                        if (TryClaimLocked(out session, out work))
                            break;
                        // No claimable work right now (empty, or every ready volume is at its cap). Wait
                        // for a state change; on idle timeout with still nothing to do, retire.
                        if (!Monitor.Wait(_lock, IdleTimeoutMs) && !TryClaimLocked(out session, out work))
                        {
                            _liveWorkers--;
                            return;
                        }
                        if (work is not null)
                            break;
                    }
                }
                Process(session!, work!);
            }
        }
        catch (Exception ex)
        {
            lock (_lock) { _liveWorkers--; }
            _logger.LogError(ex, "Scan worker crashed");
        }
    }

    // Round-robin across sessions; the first with a claimable item wins. Reserves the volume + session
    // slot before returning (released by Process via Complete/Park). Caller holds _lock.
    private bool TryClaimLocked(out ScanSession? session, out ScanWorkState? work)
    {
        int n = _sessions.Count;
        for (int i = 0; i < n; i++)
        {
            ScanSession s = _sessions[(_sessionCursor + i) % n];
            if (s.TryClaimLocked(out work))
            {
                session = s;
                _sessionCursor = (_sessionCursor + i + 1) % n;
                return true;
            }
        }
        session = null;
        work = null;
        return false;
    }

    // Caller holds _lock.
    private VolumeState EnsureVolumeLocked(string normalizedKey, DriveClass driveClass, ScanThreadingSettings threading)
    {
        if (!_volumes.TryGetValue(normalizedKey, out VolumeState? v))
        {
            int cap = Math.Max(1, ScanThreadResolver.ResolvePerDriveCap(threading, normalizedKey, driveClass));
            v = new VolumeState(normalizedKey, driveClass, cap);
            _volumes[normalizedKey] = v;
        }
        return v;
    }

    private void RemoveSessionLocked(ScanSession session)
    {
        _sessions.Remove(session);
        if (_sessions.Count == 0)
            _sessionCursor = 0;
    }

    // ---- the traversal mechanics (runs outside _lock; the I/O) ------------

    private void Process(ScanSession session, ScanWorkState work)
    {
        ScanSessionOptions opts = session.Options;
        try
        {
            if (session.IsCancelled)
            {
                session.Complete(work);
                return;
            }

            // Flush a result that was computed but couldn't be written before this directory parked.
            if (work.Pending is ScanResult pending)
            {
                if (!session.TryWrite(pending))
                {
                    Repark(session, work);
                    return;
                }
                work.Pending = null;
            }

            if (!work.Materialized)
            {
                // Materialize one directory level so no OS directory handle is held across a park.
                work.Remaining = new Queue<Result<FileSystemEntry, EnumerationFault>>(_fileSystem.EnumerateEntries(work.Directory));
                work.Materialized = true;
            }

            Queue<Result<FileSystemEntry, EnumerationFault>> queue = work.Remaining!;
            while (queue.Count > 0)
            {
                if (session.IsCancelled)
                {
                    session.Complete(work);
                    return;
                }

                Result<FileSystemEntry, EnumerationFault> next = queue.Peek();
                if (next.TryGetError(out EnumerationFault fault))
                {
                    queue.Dequeue();
                    EnumerationFault? mapped = opts.OnFault is null ? fault : opts.OnFault(fault, work.Tag);
                    if (mapped is EnumerationFault emit)
                    {
                        ScanResult result = new(null, emit, work.Tag);
                        if (!session.TryWrite(result))
                        {
                            work.Pending = result;
                            Repark(session, work);
                            return;
                        }
                    }
                    // An original Fatal is terminal for this directory; per the enumeration contract it
                    // is also the last item, so the loop ends here anyway.
                    continue;
                }

                next.TryGetValue(out FileSystemEntry? entry);
                if (entry!.IsDirectory)
                {
                    queue.Dequeue();
                    ChildDecision decision = opts.OnSubdirectory(entry, work.Tag);
                    if (decision.Descend)
                        session.Submit(new ScanWorkItem(entry.FullPath, work.VolumeKey, work.DriveClass, decision.ChildTag));
                }
                else
                {
                    bool emitFile = opts.OnFile(entry, work.Tag);   // side effects (e.g. cap reservation) run once
                    queue.Dequeue();
                    if (emitFile)
                    {
                        ScanResult result = new(entry, null, work.Tag);
                        if (!session.TryWrite(result))
                        {
                            work.Pending = result;
                            Repark(session, work);
                            return;
                        }
                    }
                }
            }

            session.Complete(work);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Scan worker faulted enumerating {Directory}", work.Directory);
            session.Complete(work);
        }
    }

    // A full output buffer while the session is live parks the directory; if the session was cancelled
    // in the meantime, drop it instead (equivalent to Complete). Caller is a worker, not holding _lock.
    private void Repark(ScanSession session, ScanWorkState work)
    {
        if (session.IsCancelled)
            session.Complete(work);
        else
            session.Park(work);
    }

    private sealed class VolumeState(string key, DriveClass driveClass, int cap)
    {
        public string Key { get; } = key;
        public DriveClass DriveClass { get; } = driveClass;
        public int Cap { get; set; } = cap;
        public int Active { get; set; }
    }

    /// <summary>Mutable per-directory work: the volume it belongs to, the adapter's tag, the
    /// materialized remaining entries, and a single result awaiting buffer space after a park.</summary>
    private sealed class ScanWorkState(string directory, string volumeKey, DriveClass driveClass, object? tag)
    {
        public string Directory { get; } = directory;
        public string VolumeKey { get; } = volumeKey;
        public DriveClass DriveClass { get; } = driveClass;
        public object? Tag { get; } = tag;
        public Queue<Result<FileSystemEntry, EnumerationFault>>? Remaining { get; set; }
        public ScanResult? Pending { get; set; }
        public bool Materialized { get; set; }
    }

    /// <summary>One logical scan. Owns its bounded output channel and its pending/parked work; shares
    /// the scheduler's worker pool and per-volume caps with every other session.</summary>
    private sealed class ScanSession : IScanSession
    {
        private readonly ScanScheduler _sched;
        private readonly ScanThreadingSettings _threading;
        private readonly Channel<ScanResult> _channel;
        private readonly CancellationTokenRegistration _ctReg;

        // Per-volume DFS stacks + a rotation over them for cross-volume fairness within the session.
        private readonly Dictionary<string, Stack<ScanWorkState>> _pending = new(StringComparer.Ordinal);
        private readonly List<string> _volumeOrder = [];
        private int _volumeCursor;
        private readonly List<ScanWorkState> _parked = [];

        private long _outstanding;   // submitted but not yet completed (includes in-flight + parked)
        private int _active;         // workers currently in this session (bounded by MaxConcurrency)
        private volatile bool _cancelled;

        public ScanSessionOptions Options { get; }
        public bool IsCancelled => _cancelled;

        public ScanSession(ScanScheduler sched, ScanSessionOptions options, ScanThreadingSettings threading, CancellationToken ct)
        {
            _sched = sched;
            Options = options;
            _threading = threading;
            _channel = Channel.CreateBounded<ScanResult>(new BoundedChannelOptions(Math.Max(1, options.OutputCapacity))
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
            });
            _ctReg = ct.CanBeCanceled ? ct.Register(static s => ((ScanSession)s!).Cancel(), this) : default;
        }

        public void Submit(ScanWorkItem item)
        {
            string key = ScanThreadResolver.NormalizeKey(item.VolumeKey);
            lock (_sched._lock)
            {
                if (_cancelled)
                    return;
                _sched.EnsureVolumeLocked(key, item.DriveClass, _threading);
                if (!_pending.TryGetValue(key, out Stack<ScanWorkState>? stack))
                {
                    stack = new Stack<ScanWorkState>();
                    _pending[key] = stack;
                    _volumeOrder.Add(key);
                }
                stack.Push(new ScanWorkState(item.Directory, key, item.DriveClass, item.Tag));
                _outstanding++;
                Monitor.PulseAll(_sched._lock);
                _sched.MaybeSpawnWorkerLocked();
            }
        }

        // Caller holds _sched._lock.
        public bool TryClaimLocked(out ScanWorkState? work)
        {
            work = null;
            if (_cancelled || _active >= Options.MaxConcurrency)
                return false;
            int m = _volumeOrder.Count;
            for (int i = 0; i < m; i++)
            {
                string key = _volumeOrder[(_volumeCursor + i) % m];
                Stack<ScanWorkState> stack = _pending[key];
                if (stack.Count == 0)
                    continue;
                VolumeState vol = _sched._volumes[key];
                if (vol.Active >= vol.Cap)
                    continue;
                work = stack.Pop();
                vol.Active++;
                _active++;
                _volumeCursor = (_volumeCursor + i + 1) % m;
                return true;
            }
            return false;
        }

        public bool TryWrite(ScanResult result) => _channel.Writer.TryWrite(result);

        // A worker finished (or abandoned) a directory: release its slots and, if this was the last
        // outstanding work, complete the output. Caller is a worker (not holding _lock).
        public void Complete(ScanWorkState work)
        {
            lock (_sched._lock)
            {
                _active--;
                _sched._volumes[work.VolumeKey].Active--;
                _outstanding--;
                FinalizeIfDrainedLocked();
                Monitor.PulseAll(_sched._lock);
            }
        }

        // A full buffer parked this directory: release its slots but keep it outstanding until the
        // consumer re-arms it. Caller is a worker (not holding _lock).
        public void Park(ScanWorkState work)
        {
            lock (_sched._lock)
            {
                _active--;
                _sched._volumes[work.VolumeKey].Active--;
                if (_cancelled)
                {
                    _outstanding--;
                    FinalizeIfDrainedLocked();
                }
                else
                {
                    _parked.Add(work);
                }
                Monitor.PulseAll(_sched._lock);
            }
        }

        // The consumer read an item (freeing a slot): move any parked work back to the pending stacks
        // so workers resume it. Called on the consumer thread.
        private void OnConsumed()
        {
            lock (_sched._lock)
            {
                if (_cancelled || _parked.Count == 0)
                    return;
                foreach (ScanWorkState w in _parked)
                {
                    if (!_pending.TryGetValue(w.VolumeKey, out Stack<ScanWorkState>? stack))
                    {
                        stack = new Stack<ScanWorkState>();
                        _pending[w.VolumeKey] = stack;
                        _volumeOrder.Add(w.VolumeKey);
                    }
                    stack.Push(w);
                }
                _parked.Clear();
                Monitor.PulseAll(_sched._lock);
                _sched.MaybeSpawnWorkerLocked();
            }
        }

        // Caller holds _sched._lock.
        private void FinalizeIfDrainedLocked()
        {
            if (_outstanding == 0)
            {
                _channel.Writer.TryComplete();
                _sched.RemoveSessionLocked(this);
            }
        }

        public IEnumerable<ScanResult> Consume()
        {
            ChannelReader<ScanResult> reader = _channel.Reader;
            while (true)
            {
                while (reader.TryRead(out ScanResult item))
                {
                    OnConsumed();
                    yield return item;
                }
                if (!WaitToRead(reader))
                    yield break;
            }
        }

        private static bool WaitToRead(ChannelReader<ScanResult> reader)
        {
            try
            {
                return reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        public void Cancel()
        {
            lock (_sched._lock)
            {
                if (_cancelled)
                    return;
                _cancelled = true;
                // Drop everything queued or parked; in-flight items will decrement outstanding as their
                // workers finish. Completing the writer unblocks a consumer mid-pull.
                foreach (Stack<ScanWorkState> stack in _pending.Values)
                {
                    _outstanding -= stack.Count;
                    stack.Clear();
                }
                _outstanding -= _parked.Count;
                _parked.Clear();
                _channel.Writer.TryComplete();
                if (_outstanding <= 0)
                    _sched.RemoveSessionLocked(this);
                Monitor.PulseAll(_sched._lock);
            }
        }

        public void Dispose()
        {
            Cancel();
            _ctReg.Dispose();
        }
    }
}
