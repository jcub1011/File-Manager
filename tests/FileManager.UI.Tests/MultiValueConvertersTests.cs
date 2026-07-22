using System.Globalization;
using FileManager.UI.Converters;

namespace FileManager.UI.Tests;

public sealed class MultiValueConvertersTests
{
    private static readonly Guid RowA = Guid.NewGuid();
    private static readonly Guid RowB = Guid.NewGuid();

    private static bool RowSelectable(bool isDirty, Guid rowId, Guid? dirtyId) =>
        MultiValueConverters.RowSelectable.Convert(
            [isDirty, rowId, dirtyId], typeof(bool), null, CultureInfo.InvariantCulture) is true;

    [Fact]
    public void Clean_editor_leaves_every_row_selectable()
    {
        Assert.True(RowSelectable(isDirty: false, RowA, dirtyId: null));
        Assert.True(RowSelectable(isDirty: false, RowB, dirtyId: RowA));
    }

    [Fact]
    public void Dirty_existing_profile_locks_every_row_but_its_own()
    {
        Assert.True(RowSelectable(isDirty: true, RowA, dirtyId: RowA));    // the edited row stays open
        Assert.False(RowSelectable(isDirty: true, RowB, dirtyId: RowA));   // siblings lock
    }

    [Fact]
    public void Dirty_new_draft_with_no_id_locks_all_existing_rows()
    {
        Assert.False(RowSelectable(isDirty: true, RowA, dirtyId: null));
        Assert.False(RowSelectable(isDirty: true, RowB, dirtyId: null));
    }
}
