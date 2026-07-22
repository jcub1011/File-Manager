using FileManager.Contracts;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace FileManager.UI.Extensions;

public static class EnumTitleExtensions
{
    /// <summary>The generic type parameter (rather than reflecting on a boxed value) is what keeps this
    /// trim-safe under the UI's NativeAOT publish: the trimmer preserves field metadata per concrete
    /// instantiation instead of having to reason about an arbitrary runtime type.</summary>
    public static string GetTitle<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields)] TEnum>(this TEnum value)
        where TEnum : struct, Enum
    {
        FieldInfo? field = typeof(TEnum).GetField(value.ToString());
        TooltipAttribute? attr = field?.GetCustomAttribute<TooltipAttribute>();
        return attr?.Title ?? value.ToString();
    }
}
