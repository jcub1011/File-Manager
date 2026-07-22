using System;

namespace FileManager.Contracts.IPC;

public static class IpcEndpoint
{
    /// <summary>Environment override for the pipe name — dev/test isolation. The server-side
    /// provider honors the same variable so an overridden client still finds its server.</summary>
    public const string PipeNameOverrideVariable = "FILEMANAGER_PIPE_NAME";

    /// <summary>Client-side endpoint name: the pipe name "filemanager-&lt;user&gt;" on Windows
    /// (NamedPipeClientStream takes the bare name, server "."); $XDG_RUNTIME_DIR/filemanager.sock
    /// on Linux [seam]. Deliberately duplicates the server-side derivation in Core.Platform's
    /// IIpcEndpointProvider (§4.11 note) — Contracts cannot see Core.</summary>
    public static string Resolve()
    {
        string? overridden = Environment.GetEnvironmentVariable(PipeNameOverrideVariable);
        if (!string.IsNullOrWhiteSpace(overridden))
            return overridden;
        return "filemanager-" + SanitizeUserName(Environment.UserName);
    }

    /// <summary>Lowercases and replaces anything outside [a-z0-9-_] with '-' so the user name is
    /// always a legal pipe-name segment.</summary>
    public static string SanitizeUserName(string userName)
    {
        ArgumentNullException.ThrowIfNull(userName);
        char[] chars = userName.ToLowerInvariant().ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            char c = chars[i];
            bool legal = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || c == '_';
            if (!legal)
                chars[i] = '-';
        }
        return new string(chars);
    }
}
