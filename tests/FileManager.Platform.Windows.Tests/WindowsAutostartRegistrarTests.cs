using FileManager.Platform.Windows;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using System.Runtime.Versioning;

namespace FileManager.Platform.Windows.Tests;

[SupportedOSPlatform("windows")]
public sealed class WindowsAutostartRegistrarTests : IDisposable
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "FileManager.Service";

    // Preserve any real value so the test never clobbers a genuine autostart entry.
    private readonly object? _priorValue;

    public WindowsAutostartRegistrarTests()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        _priorValue = key?.GetValue(ValueName);
    }

    public void Dispose()
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        if (_priorValue is null)
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        else
            key.SetValue(ValueName, _priorValue);
    }

    private static object? ReadValue()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(ValueName);
    }

    [Fact]
    public void Register_then_unregister_writes_and_removes_the_run_value()
    {
        WindowsAutostartRegistrar registrar = new(NullLogger<WindowsAutostartRegistrar>.Instance);

        Assert.True(registrar.RegisterAutostart().IsSuccess);
        object? written = ReadValue();
        Assert.NotNull(written);
        // The registrar records the current process path, quoted. Under the test host that is the
        // runner, not FileManager.Service.exe — assert the quoting/path contract, not the exe name.
        Assert.Equal($"\"{Environment.ProcessPath}\"", (string)written!);

        Assert.True(registrar.UnregisterAutostart().IsSuccess);
        Assert.Null(ReadValue());
    }

    [Fact]
    public void Register_is_idempotent()
    {
        WindowsAutostartRegistrar registrar = new(NullLogger<WindowsAutostartRegistrar>.Instance);

        Assert.True(registrar.RegisterAutostart().IsSuccess);
        Assert.True(registrar.RegisterAutostart().IsSuccess);        // second call is a no-op success
        Assert.NotNull(ReadValue());

        // Unregistering when absent is also a success.
        Assert.True(registrar.UnregisterAutostart().IsSuccess);
        Assert.True(registrar.UnregisterAutostart().IsSuccess);
    }
}
