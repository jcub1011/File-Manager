using System;

namespace FileManager.Contracts;

/// <summary>Decorates an enum member with UI display text. <see cref="Title"/> is the label shown in
/// place of the raw member name; <see cref="Tooltip"/> is optional hover text for a future UI feature.</summary>
[AttributeUsage(AttributeTargets.Field)]
public sealed class TooltipAttribute(string title, string? tooltip = null) : Attribute
{
    public string Title { get; } = title;
    public string? Tooltip { get; } = tooltip;
}
