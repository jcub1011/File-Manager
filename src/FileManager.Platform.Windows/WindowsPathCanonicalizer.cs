using FileManager.Core.Platform;
using Microsoft.Extensions.Logging;
using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace FileManager.Platform.Windows;

/// <summary>Expands 8.3 short names via <c>GetLongPathNameW</c>. Best-effort by contract: a path
/// that does not exist (profiles may point at not-yet-created folders), an access-denied segment,
/// or any Win32 failure returns the input unchanged — canonicalization must never make a valid
/// path unusable.</summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsPathCanonicalizer(ILogger<WindowsPathCanonicalizer> logger) : IPathCanonicalizer
{
    public string Canonicalize(string absolutePath)
    {
        try
        {
            // A short-name segment always contains '~'; skip the syscall for the common case.
            if (!absolutePath.Contains('~'))
                return absolutePath;

            char[] buffer = new char[absolutePath.Length + 64];
            uint length = GetLongPathNameW(absolutePath, buffer, (uint)buffer.Length);
            if (length > buffer.Length)
            {
                buffer = new char[length];
                length = GetLongPathNameW(absolutePath, buffer, (uint)buffer.Length);
            }
            if (length == 0 || length > buffer.Length)
                return absolutePath;   // nonexistent / inaccessible — nothing to expand against
            return new string(buffer, 0, (int)length);
        }
        catch (Exception ex)
        {
            // Last-resort catch-all (directive): expansion is an optional refinement, never fatal.
            logger.LogWarning(ex, "Long-name expansion failed for {Path}; using it as written", absolutePath);
            return absolutePath;
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetLongPathNameW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetLongPathNameW(string lpszShortPath, [Out] char[] lpszLongPath, uint cchBuffer);
}
