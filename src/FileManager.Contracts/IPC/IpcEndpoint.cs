using System;

namespace FileManager.Contracts.IPC;

public static class IpcEndpoint
{
    /// <summary>Client-side endpoint name: \\.\pipe\filemanager-&lt;user&gt; on Windows;
    /// $XDG_RUNTIME_DIR/filemanager.sock on Linux [seam]. Deliberately duplicates the server-side
    /// derivation in Core.Platform's IIpcEndpointProvider (§4.11 note) — Contracts cannot see Core.</summary>
    public static string Resolve() => throw new NotImplementedException();
}
