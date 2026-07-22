using FileManager.Contracts.IPC;
using System;

namespace FileManager.Core.Observability;

public interface IEngineEventBus
{
    void Publish(EngineEvent evt);
    IDisposable Subscribe(Action<EngineEvent> handler);
}
