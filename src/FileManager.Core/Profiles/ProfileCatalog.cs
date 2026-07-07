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
    private volatile IReadOnlyList<Profile> _all = [];
    private volatile IReadOnlyList<Profile> _active = [];

    public IReadOnlyList<Profile> All => _all;

    public IReadOnlyList<Profile> Active => _active;

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

        _all = profiles!;
        _active = profiles!.Where(p => p.Active).ToList();
        logger.LogInformation("Profile catalog reloaded: {Total} profiles, {Active} active",
            _all.Count, _active.Count);

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
