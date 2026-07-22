namespace FileManager.Core.Platform;

/// <summary>Expands OS-specific path aliases to their canonical form — on Windows, 8.3 short names
/// (<c>C:\PROGRA~1</c> → <c>C:\Program Files</c>). Applied where user-authored paths enter the
/// system (profile validation), so one physical location cannot be admitted under two spellings
/// and defeat every identity built on <see cref="Jobs.NormalizedPath"/> — path locks, session
/// priorities, self-write suppression, overlap warnings.</summary>
public interface IPathCanonicalizer
{
    /// <summary>The canonical form of <paramref name="absolutePath"/>, or the input unchanged when
    /// the path does not exist (nothing to expand against) or expansion is unavailable. Never throws.</summary>
    string Canonicalize(string absolutePath);
}
