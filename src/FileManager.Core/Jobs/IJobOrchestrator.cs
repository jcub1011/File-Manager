using FileManager.Contracts.IPC;
using FileManager.Contracts.Primitives;
using System.Threading.Tasks;

namespace FileManager.Core.Jobs;

public interface IJobOrchestrator
{
    Result Start();
    Task StopAsync();
    EngineStatusSnapshot GetStatus();
}
