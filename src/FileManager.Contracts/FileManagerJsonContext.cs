using FileManager.Contracts.DryRun;
using FileManager.Contracts.IPC;
using FileManager.Contracts.Profiles;
using FileManager.Contracts.Settings;
using System.Text.Json.Serialization;

namespace FileManager.Contracts;

[JsonSourceGenerationOptions(WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(Profile))]
[JsonSerializable(typeof(IpcRequest))]
[JsonSerializable(typeof(IpcResponse))]
[JsonSerializable(typeof(EngineEvent))]
[JsonSerializable(typeof(DryRunReport))]
[JsonSerializable(typeof(DryRunDirectory))]
[JsonSerializable(typeof(DryRunFile))]
[JsonSerializable(typeof(DryRunOperation))]
// Also serialized standalone: DryRunEngine measures each physical file against its report byte budget.
[JsonSerializable(typeof(PhysicalFile))]
// Also serialized standalone: DryRunEngine measures each operation against the same budget.
[JsonSerializable(typeof(VirtualFileOperation))]
// Also serialized standalone: SettingsService persists it to settings.json.
[JsonSerializable(typeof(GlobalSettings))]
public sealed partial class FileManagerJsonContext : JsonSerializerContext;
