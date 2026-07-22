using FileManager.Contracts.Primitives;
using FileManager.Contracts.Profiles;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FileManager.Core.Profiles;

/// <summary>In-memory authoritative set of loaded profiles (§4.1). Reload swaps an immutable
/// snapshot and notifies subscribers — the seam the watcher/scheduler re-arm on when they land.</summary>
public sealed class ProfileCatalog(ILogger<ProfileCatalog> logger, IProfileStore store) : IProfileCatalog
{
    private readonly object _gate = new();
    private readonly List<Subscription> _subscriptions = [];
    // One volatile snapshot holding BOTH lists: publishing them as two independent fields lets a
    // reader between the writes observe a mismatched pair (e.g. a just-deleted profile absent from
    // All but still in Active), which a future watcher/scheduler re-arm would act on.
    private volatile Snapshot _snapshot = new([], []);

    public IReadOnlyList<Profile> All => _snapshot.All;

    public IReadOnlyList<Profile> Active => _snapshot.Active;

    private sealed record Snapshot(IReadOnlyList<Profile> All, IReadOnlyList<Profile> Active);

    public IDisposable Subscribe(Action changeHandler)
    {
        ArgumentNullException.ThrowIfNull(changeHandler);
        Subscription subscription = new(this, changeHandler);
        lock (_gate)
            _subscriptions.Add(subscription);
        return subscription;
    }

    public Result Reload()
    {
        Result<IReadOnlyList<Profile>, string> loaded = store.LoadAll();
        if (loaded.TryGetError(out string? error))
            return error;
        loaded.TryGetValue(out IReadOnlyList<Profile>? profiles);

        Snapshot next = new(profiles!, profiles!.Where(p => p.Active).ToList());
        _snapshot = next;
        logger.LogInformation("Profile catalog reloaded: {Total} profiles, {Active} active",
            next.All.Count, next.Active.Count);

        Subscription[] snapshot;
        lock (_gate)
            snapshot = [.. _subscriptions];
        foreach (Subscription subscription in snapshot)
        {
            try
            {
                subscription.Handler();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "A profile-catalog subscriber threw; continuing with the rest");
            }
        }
        return Result.Success();
    }

    private void Unsubscribe(Subscription subscription)
    {
        lock (_gate)
            _subscriptions.Remove(subscription);
    }

    private sealed class Subscription(ProfileCatalog owner, Action handler) : IDisposable
    {
        public Action Handler { get; } = handler;

        public void Dispose() => owner.Unsubscribe(this);
    }
}
