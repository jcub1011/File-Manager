using System.Text.Json.Serialization;

namespace FileManager.Core.Watching;

/// <summary>Persisted global pause flag (spec §3.2.4, §9): <c>state/pause.json</c> is
/// <c>{ "paused": true }</c>.</summary>
internal sealed record PersistedPauseState
{
    public bool Paused { get; init; }
}

/// <summary>Source-generated, AOT-safe serialization for the engine's small <c>state/</c> files.
/// Internal so these on-disk shapes never leak into the public wire surface
/// (<c>FileManagerJsonContext</c>). Set 5's <c>schedule.json</c> reuses this context.</summary>
[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(PersistedPauseState))]
internal sealed partial class StateJsonContext : JsonSerializerContext;
