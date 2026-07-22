using FileManager.Contracts.Primitives;
using System;

namespace FileManager.Core.Watching;

public interface ISchedulerService
{
    Result Start();
    void Stop();
    IDisposable Subscribe(Action<ScheduleTick> dueHandler);
}

public readonly record struct ScheduleTick(Guid ProfileId, DateTimeOffset ScheduledFor, bool IsCatchUp);
