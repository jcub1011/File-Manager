using System.Text.Json;

namespace FileManager.Contracts.IPC;

/// <summary>The single choke-point for IPC message (de)serialization, shared by the client here
/// and the server in Core. Everything goes through the BASE-type JsonTypeInfo: serializing a
/// message as its derived type omits the "type" discriminator under source generation, which
/// silently breaks the wire format — this class exists to make that mistake impossible.</summary>
public static class IpcSerializer
{
    public static byte[] SerializeRequest(IpcRequest request) =>
        JsonSerializer.SerializeToUtf8Bytes(request, FileManagerJsonContext.Default.IpcRequest);

    public static byte[] SerializeResponse(IpcResponse response) =>
        JsonSerializer.SerializeToUtf8Bytes(response, FileManagerJsonContext.Default.IpcResponse);

    public static byte[] SerializeEvent(EngineEvent evt) =>
        JsonSerializer.SerializeToUtf8Bytes(evt, FileManagerJsonContext.Default.EngineEvent);

    /// <summary>Null on malformed JSON or an unknown discriminator (never throws for bad input from the wire).</summary>
    public static IpcRequest? DeserializeRequest(byte[] payload)
    {
        try { return JsonSerializer.Deserialize(payload, FileManagerJsonContext.Default.IpcRequest); }
        catch (JsonException) { return null; }
    }

    /// <summary>Null on malformed JSON or an unknown discriminator (never throws for bad input from the wire).</summary>
    public static IpcResponse? DeserializeResponse(byte[] payload)
    {
        try { return JsonSerializer.Deserialize(payload, FileManagerJsonContext.Default.IpcResponse); }
        catch (JsonException) { return null; }
    }

    /// <summary>Null on malformed JSON or an unknown discriminator (never throws for bad input from the wire).</summary>
    public static EngineEvent? DeserializeEvent(byte[] payload)
    {
        try { return JsonSerializer.Deserialize(payload, FileManagerJsonContext.Default.EngineEvent); }
        catch (JsonException) { return null; }
    }
}
