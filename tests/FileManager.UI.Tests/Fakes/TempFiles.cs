using System;
using System.IO;

namespace FileManager.UI.Tests.Fakes;

internal static class TempFiles
{
    /// <summary>An isolated client-settings path for one test. Every view model that touches client
    /// settings takes this as a seam, because the real one lives in the developer's %LOCALAPPDATA% —
    /// a test that read it would depend on whatever theme the machine happens to be set to, and a test
    /// that saved would overwrite it.
    /// <para>Not created, and not cleaned up: the store treats a missing file as "defaults", so most
    /// tests never write one at all, and a stray few hundred bytes in TEMP is a better trade than a
    /// fixture on every test class.</para></summary>
    public static string ClientSettings() =>
        Path.Combine(Path.GetTempPath(), "fm-client-" + Guid.NewGuid().ToString("N") + ".json");
}
