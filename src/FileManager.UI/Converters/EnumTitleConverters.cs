using Avalonia.Data.Converters;
using FileManager.Contracts.DryRun;
using FileManager.Contracts.Profiles;
using FileManager.Contracts.Settings;
using FileManager.UI.Extensions;

namespace FileManager.UI.Converters;

/// <summary>One strongly-typed converter instance per enum type, for use via <c>x:Static</c> in XAML
/// bindings (see <c>ObjectConverters.IsNotNull</c> usage elsewhere for the same pattern). Each lambda
/// closes over its enum type at compile time, so — unlike a single converter reflecting on a boxed
/// <c>object</c> — the AOT trimmer can resolve exactly which enum's field metadata to keep.</summary>
public static class EnumTitleConverters
{
    public static readonly FuncValueConverter<SyncMode, string> SyncMode = new(v => v.GetTitle());
    public static readonly FuncValueConverter<TargetLayout, string> TargetLayout = new(v => v.GetTitle());
    public static readonly FuncValueConverter<ConflictResolution, string> ConflictResolution = new(v => v.GetTitle());
    public static readonly FuncValueConverter<OverwriteHandling, string> OverwriteHandling = new(v => v.GetTitle());
    public static readonly FuncValueConverter<VerificationMethod, string> VerificationMethod = new(v => v.GetTitle());
    public static readonly FuncValueConverter<OnSuccessAction, string> OnSuccessAction = new(v => v.GetTitle());
    public static readonly FuncValueConverter<MetadataOnConflict, string> MetadataOnConflict = new(v => v.GetTitle());
    public static readonly FuncValueConverter<LogVerbosity, string> LogVerbosity = new(v => v.GetTitle());
    public static readonly FuncValueConverter<ConcurrencyMode, string> ConcurrencyMode = new(v => v.GetTitle());
    public static readonly FuncValueConverter<ServiceStartupMode, string> ServiceStartupMode = new(v => v.GetTitle());
    public static readonly FuncValueConverter<ThemeMode, string> ThemeMode = new(v => v.GetTitle());
}
