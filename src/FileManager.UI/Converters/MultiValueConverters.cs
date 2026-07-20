using Avalonia.Data.Converters;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace FileManager.UI.Converters;

/// <summary>Multi-binding converters used via <c>x:Static</c> in XAML.</summary>
public static class MultiValueConverters
{
    /// <summary>True only when both bound values are the same non-null <see cref="Guid"/>. Used to
    /// light a sidebar row's unsaved marker when its ProfileId equals the list's UnsavedProfileId
    /// (a null id — no unsaved profile — never matches).</summary>
    public static readonly IMultiValueConverter GuidEquals = new GuidEqualsConverter();

    private sealed class GuidEqualsConverter : IMultiValueConverter
    {
        public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture) =>
            values.Count == 2 && values[0] is Guid a && values[1] is Guid b && a == b;
    }

    /// <summary>Whether a profile-list row may be selected. Inputs: [0] the editor's dirty state
    /// (bool), [1] this row's ProfileId (Guid), [2] the dirty profile's id (Guid?, null for a
    /// never-saved draft). A clean editor leaves every row selectable; while a draft is dirty only
    /// its own row stays enabled — a new (idless) draft matches nothing, so every existing row locks
    /// until the edits are saved or discarded. This stops the user appearing to navigate away.</summary>
    public static readonly IMultiValueConverter RowSelectable = new RowSelectableConverter();

    private sealed class RowSelectableConverter : IMultiValueConverter
    {
        public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
        {
            if (values.Count != 3 || values[0] is not bool isDirty || !isDirty)
                return true;   // clean editor (or a not-yet-populated binding): all rows selectable
            return values[1] is Guid rowId && values[2] is Guid dirtyId && rowId == dirtyId;
        }
    }
}
