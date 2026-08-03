using FileManager.Contracts.Primitives;
using System;
using System.Buffers;
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

    /// <summary>Serializes into a caller-owned buffer instead of a fresh exact-size <c>byte[]</c>.
    ///
    /// <para>Exists for the server's response path, where one connection can emit thousands of frames
    /// in a single streamed dry run. <see cref="SerializeResponse(IpcResponse)"/> allocates a new array
    /// per frame; anything from 85,000 bytes lands on the Large Object Heap, which is uncompacted by
    /// default and only collected with a gen2, so the churn becomes lasting committed memory rather
    /// than a transient write. A reused writer turns all of it into one buffer that grows once.</para>
    ///
    /// <para>Goes through the BASE-type <c>JsonTypeInfo</c> for the same reason the whole class does:
    /// serializing as the derived type would drop the <c>"type"</c> discriminator under source
    /// generation and silently break the wire format.</para></summary>
    public static void SerializeResponse(IpcResponse response, IBufferWriter<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        using Utf8JsonWriter writer = new(destination);
        JsonSerializer.Serialize(writer, response, FileManagerJsonContext.Default.IpcResponse);
    }

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

    /// <summary>Deserializes from a borrowed region instead of an exact-size <c>byte[]</c> — the read-side
    /// counterpart to <see cref="SerializeResponse(IpcResponse, IBufferWriter{byte})"/>, and for the same
    /// reason: a streamed dry run's chunk frames are megabytes each, so an exact-size array per frame is
    /// a Large Object Heap allocation per frame. This overload lets the client read into one reused
    /// buffer and parse the written span in place.
    ///
    /// <para>Identical semantics to the array overload, including going through the BASE-type
    /// <c>JsonTypeInfo</c> so the <c>"type"</c> discriminator is honoured.</para></summary>
    public static Result<IpcResponse, string> DeserializeResponse(ReadOnlySpan<byte> payload)
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
