using FileManager.Contracts.Primitives;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace FileManager.Core.Watching;

/// <summary>The persisted global pause flag (spec §3.2.4). Survives restart via
/// <c>state/pause.json</c> (§9). Loaded once at construction; <see cref="SetPaused"/> writes
/// atomically (temp + rename — convenience data, no fsync) and notifies subscribers so the trigger
/// queue's dequeue gate re-arms. Subscription mirrors <see cref="Profiles.ProfileCatalog"/>.</summary>
public sealed class PauseStateService : IPauseStateService
{
    private readonly EnginePaths _paths;
    private readonly ILogger<PauseStateService> _logger;
    private readonly object _gate = new();
    private readonly List<Subscription> _subscriptions = [];
    private volatile bool _paused;

    public PauseStateService(EnginePaths paths, ILogger<PauseStateService> logger)
    {
        _paths = paths;
        _logger = logger;
        _paused = Load();
    }

    public bool IsPaused => _paused;

    public Result SetPaused(bool paused)
    {
        if (_paused == paused)
            return Result.Success();

        Result persisted = Persist(paused);
        if (persisted.TryGetError(out string? error))
            return error;

        _paused = paused;
        _logger.LogInformation("Engine pause state set to {Paused}", paused);

        Subscription[] snapshot;
        lock (_gate)
            snapshot = [.. _subscriptions];
        foreach (Subscription subscription in snapshot)
        {
            try
            {
                subscription.Handler(paused);
            }
            catch (Exception ex)
            {
                // Last-resort catch-and-log: a bad subscriber must not abort the notification fan-out.
                _logger.LogError(ex, "A pause-state subscriber threw; continuing with the remaining subscribers");
            }
        }
        return Result.Success();
    }

    public IDisposable Subscribe(Action<bool> pauseHandler)
    {
        ArgumentNullException.ThrowIfNull(pauseHandler);
        Subscription subscription = new(this, pauseHandler);
        lock (_gate)
            _subscriptions.Add(subscription);
        return subscription;
    }

    private string PauseFilePath => Path.Combine(_paths.StateDirectory, "pause.json");

    private bool Load()
    {
        try
        {
            string path = PauseFilePath;
            if (!File.Exists(path))
                return false;
            byte[] bytes = File.ReadAllBytes(path);
            PersistedPauseState? state = JsonSerializer.Deserialize(bytes, StateJsonContext.Default.PersistedPauseState);
            return state?.Paused ?? false;
        }
        catch (Exception ex)
        {
            // A corrupt/unreadable pause file must not stop the engine — default to not paused.
            _logger.LogWarning(ex, "Could not read pause state; defaulting to not paused");
            return false;
        }
    }

    private Result Persist(bool paused)
    {
        try
        {
            Directory.CreateDirectory(_paths.StateDirectory);
            string path = PauseFilePath;
            string tempPath = path + ".tmp";
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
                new PersistedPauseState { Paused = paused }, StateJsonContext.Default.PersistedPauseState);
            File.WriteAllBytes(tempPath, bytes);
            File.Move(tempPath, path, overwrite: true);
            return Result.Success();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not persist pause state");
            return $"could not persist pause state: {ex.Message}";
        }
        catch (Exception ex)
        {
            // Last-resort catch-and-log.
            _logger.LogError(ex, "Unexpected error persisting pause state");
            return $"could not persist pause state: {ex.Message}";
        }
    }

    private void Unsubscribe(Subscription subscription)
    {
        lock (_gate)
            _subscriptions.Remove(subscription);
    }

    private sealed class Subscription(PauseStateService owner, Action<bool> handler) : IDisposable
    {
        public Action<bool> Handler { get; } = handler;

        public void Dispose() => owner.Unsubscribe(this);
    }
}
