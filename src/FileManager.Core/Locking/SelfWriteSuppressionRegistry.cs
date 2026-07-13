using FileManager.Core.Jobs;
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading;

namespace FileManager.Core.Locking;

/// <summary>Spec §3.2.3 rule 2. Every path the engine is about to write is registered before the
/// first byte; the watcher/settle path drops events for suppressed paths so the engine's own
/// writes never echo back as new work. Disposal starts a linger window (default one settle
/// window, 2 s) so a *different* profile watching the target still sees the file once the window
/// lapses (deliberate chaining).</summary>
public sealed class SelfWriteSuppressionRegistry : IDisposable
{
    /// <summary>Default linger — one settle window (§4.3). The engine does not know each path's
    /// owning-Source SettleDelaySeconds here, so the conservative default is used unless a caller
    /// passes an explicit window to <see cref="SuppressionToken.Release"/>.</summary>
    internal static readonly TimeSpan DefaultLinger = TimeSpan.FromSeconds(2);

    private const long ActiveSentinel = long.MaxValue;   // expiresAtTicks while the registration is live

    /// <summary>Ceiling on how long an <c>Active</c> registration may suppress a path. A job that
    /// faults on a path bypassing its <c>Release</c>/<c>Dispose</c> would otherwise suppress that path
    /// for the process lifetime, so the watcher would silently drop real events for it forever. Well
    /// above any legitimate single-file operation.</summary>
    private static readonly TimeSpan MaxActiveTtl = TimeSpan.FromHours(1);

    private sealed class Entry
    {
        public required JobId Owner { get; init; }
        public long RegisteredAtTicks { get; init; }
        public long ExpiresAtTicks;
    }

    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<NormalizedPath, Entry> _entries = new();
    private readonly ITimer _sweep;

    public SelfWriteSuppressionRegistry(TimeProvider time)
    {
        _time = time;
        // Opportunistic periodic sweep so a path the workload stops touching does not linger in
        // the map forever; IsSuppressed also prunes lazily on access.
        _sweep = time.CreateTimer(
            static s => ((SelfWriteSuppressionRegistry)s!).Sweep(), this,
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    public SuppressionToken Register(NormalizedPath path, JobId owner)
    {
        _entries[path] = new Entry { Owner = owner, RegisteredAtTicks = _time.GetUtcNow().UtcTicks, ExpiresAtTicks = ActiveSentinel };
        return new SuppressionToken(this, path, owner);
    }

    /// <summary>True while the registration is active OR within its post-release linger window.</summary>
    public bool IsSuppressed(NormalizedPath path)
    {
        if (!_entries.TryGetValue(path, out Entry? entry))
            return false;
        long now = _time.GetUtcNow().UtcTicks;
        long expires = Interlocked.Read(ref entry.ExpiresAtTicks);
        if (expires == ActiveSentinel)
        {
            // Active — but bounded: past the max-active TTL a never-released registration is treated
            // as expired so a faulted job cannot suppress the path forever.
            if (now - entry.RegisteredAtTicks <= MaxActiveTtl.Ticks)
                return true;
            _entries.TryRemove(new KeyValuePair<NormalizedPath, Entry>(path, entry));
            return false;
        }
        if (now < expires)
            return true;
        // Expired: prune lazily (only removes this exact entry, so a concurrent re-Register wins).
        _entries.TryRemove(new KeyValuePair<NormalizedPath, Entry>(path, entry));
        return false;
    }

    internal void StartLinger(NormalizedPath path, JobId owner, TimeSpan lingerWindow)
    {
        if (!_entries.TryGetValue(path, out Entry? entry) || !entry.Owner.Equals(owner))
            return;   // re-registered by a later job, or already gone — leave it alone
        long expiresAt = _time.GetUtcNow().UtcTicks + Math.Max(0, lingerWindow.Ticks);
        Interlocked.Exchange(ref entry.ExpiresAtTicks, expiresAt);
    }

    private void Sweep()
    {
        long now = _time.GetUtcNow().UtcTicks;
        foreach (KeyValuePair<NormalizedPath, Entry> kvp in _entries)
        {
            long expires = Interlocked.Read(ref kvp.Value.ExpiresAtTicks);
            bool lingerExpired = expires != ActiveSentinel && now >= expires;
            bool activeExpired = expires == ActiveSentinel && now - kvp.Value.RegisteredAtTicks > MaxActiveTtl.Ticks;
            if (lingerExpired || activeExpired)
                _entries.TryRemove(kvp);
        }
    }

    public void Dispose() => _sweep.Dispose();
}

public sealed class SuppressionToken(SelfWriteSuppressionRegistry registry, NormalizedPath path, JobId owner) : IDisposable
{
    public NormalizedPath Path => path;

    public void Release(TimeSpan lingerWindow) => registry.StartLinger(path, owner, lingerWindow);

    void IDisposable.Dispose() => registry.StartLinger(path, owner, SelfWriteSuppressionRegistry.DefaultLinger);
}
