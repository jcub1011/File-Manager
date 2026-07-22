using FileManager.Contracts.Primitives;
using System;
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

    /// <summary>Failure on malformed JSON or an unknown discriminator, carrying the parse detail
    /// so callers can log why the frame was rejected (never throws for bad input from the wire).</summary>
    public static Result<IpcRequest, string> DeserializeRequest(byte[] payload)
    {
        try
        {
            IpcRequest? parsed = JsonSerializer.Deserialize(payload, FileManagerJsonContext.Default.IpcRequest);
            if (parsed is null)
                return "the request payload deserialized to null";
            return parsed;
        }
        catch (JsonException ex)
        {
            return $"malformed request: {ex.Message}";
        }
        catch (Exception ex)
        {
            // Last resort: an unexpected exception becomes a traceable failure value (callers
            // log every failure).
            return $"request deserialization failed unexpectedly: {ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <summary>Failure on malformed JSON or an unknown discriminator, carrying the parse detail
    /// so callers can log why the frame was rejected (never throws for bad input from the wire).</summary>
    public static Result<IpcResponse, string> DeserializeResponse(byte[] payload)
    {
        try
        {
            IpcResponse? parsed = JsonSerializer.Deserialize(payload, FileManagerJsonContext.Default.IpcResponse);
            if (parsed is null)
                return "the response payload deserialized to null";
            return parsed;
        }
        catch (JsonException ex)
        {
            return $"malformed response: {ex.Message}";
        }
        catch (Exception ex)
        {
            // Last resort: an unexpected exception becomes a traceable failure value (callers
            // log every failure).
            return $"response deserialization failed unexpectedly: {ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <summary>Failure on malformed JSON or an unknown discriminator, carrying the parse detail
    /// so callers can log why the frame was rejected (never throws for bad input from the wire).</summary>
    public static Result<EngineEvent, string> DeserializeEvent(byte[] payload)
    {
        try
        {
            EngineEvent? parsed = JsonSerializer.Deserialize(payload, FileManagerJsonContext.Default.EngineEvent);
            if (parsed is null)
                return "the event payload deserialized to null";
            return parsed;
        }
        catch (JsonException ex)
        {
            return $"malformed event: {ex.Message}";
        }
        catch (Exception ex)
        {
            // Last resort: an unexpected exception becomes a traceable failure value (callers
            // log every failure).
            return $"event deserialization failed unexpectedly: {ex.GetType().Name}: {ex.Message}";
        }
    }
}
