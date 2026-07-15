using FileManager.Contracts;

namespace FileManager.Contracts.Settings;

/// <summary>The UI theme the user selects, edited in the UI and persisted in
/// <see cref="GlobalSettings"/>. Applied by setting the Avalonia application's requested theme
/// variant.</summary>
public enum ThemeMode
{
    /// <summary>Follow the operating system's light/dark preference. This is the default, and is
    /// deliberately the zero value so any initializer-bypassing path lands on the OS-follow behavior
    /// (the app's historical behavior) rather than forcing a fixed theme.</summary>
    [Tooltip("System")]
    System,

    /// <summary>Always use the light theme regardless of the OS preference.</summary>
    [Tooltip("Light")]
    Light,

    /// <summary>Always use the dark theme regardless of the OS preference.</summary>
    [Tooltip("Dark")]
    Dark,
}
