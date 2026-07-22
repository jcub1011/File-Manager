using FileManager.Contracts.DryRun;
using FileManager.Contracts.IPC;
using FileManager.Contracts.Profiles;
using FileManager.Contracts.Settings;
using System.Collections.Generic;
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
// Also serialized standalone: SettingsService persists it to settings.json.
[JsonSerializable(typeof(GlobalSettings))]
[JsonSerializable(typeof(ScanThreadingSettings))]
[JsonSerializable(typeof(ThreadBudget))]
[JsonSerializable(typeof(Dictionary<DriveClass, ThreadBudget>))]
[JsonSerializable(typeof(Dictionary<string, ThreadBudget>))]
public sealed partial class FileManagerJsonContext : JsonSerializerContext;
