using FileManager.Contracts.Primitives;
using System;

namespace FileManager.Core.Watching;

public interface IPauseStateService
{
    bool IsPaused { get; }
    Result SetPaused(bool paused);
    IDisposable Subscribe(Action<bool> pauseHandler);
}
