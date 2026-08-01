using CommunityToolkit.Mvvm.ComponentModel;
using FileManager.UI.Undo;
using System.Collections.ObjectModel;

namespace FileManager.UI.Tests;

/// <summary>The undo mechanism on its own, with throwaway view models rather than the settings catalog —
/// these are the guarantees every future consumer (the profile editor next) will rely on.</summary>
public sealed partial class UndoHistoryTests
{
    /// <summary>A minimal tracked view model: one coalescing text property, one discrete flag, and a
    /// number.</summary>
    private sealed partial class Editable : ObservableObject, IUndoTrackable
    {
        [ObservableProperty] public partial string Text { get; set; } = "";
        [ObservableProperty] public partial bool Flag { get; set; }
        [ObservableProperty] public partial int Number { get; set; }

        public IEnumerable<UndoableProperty> UndoableProperties =>
        [
            UndoableProperty.For(nameof(Text), () => Text, v => Text = v, coalesce: true),
            UndoableProperty.For(nameof(Flag), () => Flag, v => Flag = v),
            UndoableProperty.For(nameof(Number), () => Number, v => Number = v, coalesce: true),
        ];

        /// <summary>Re-raises a change notification without the value having moved — what a computed
        /// property does when its source changes (see <c>ChoiceSettingViewModel</c>'s typed Value).</summary>
        public void Announce(string propertyName) => OnPropertyChanged(propertyName);
    }

    private sealed partial class Row : ObservableObject, IUndoTrackable
    {
        [ObservableProperty] public partial string Name { get; set; } = "";

        public IEnumerable<UndoableProperty> UndoableProperties =>
            [UndoableProperty.For(nameof(Name), () => Name, v => Name = v, coalesce: true)];
    }

    private static (UndoHistory History, Editable Target) NewTracked()
    {
        UndoHistory history = new();
        Editable target = new();
        history.Track(target);
        history.Reset();
        return (history, target);
    }

    // ============================ Property changes ============================

    [Fact]
    public void Undo_restores_the_previous_value()
    {
        (UndoHistory history, Editable target) = NewTracked();
        target.Flag = true;

        Assert.True(history.CanUndo);
        history.UndoCommand.Execute(null);

        Assert.False(target.Flag);
        Assert.False(history.CanUndo);
        Assert.True(history.CanRedo);
    }

    [Fact]
    public void Redo_reapplies_an_undone_change()
    {
        (UndoHistory history, Editable target) = NewTracked();
        target.Flag = true;
        history.UndoCommand.Execute(null);

        history.RedoCommand.Execute(null);

        Assert.True(target.Flag);
        Assert.False(history.CanRedo);
    }

    [Fact]
    public void Applying_an_undo_does_not_record_a_new_change()
    {
        (UndoHistory history, Editable target) = NewTracked();
        target.Flag = true;
        history.UndoCommand.Execute(null);

        Assert.Equal(0, history.UndoDepth);      // the restore itself was not recorded
        Assert.Equal(1, history.RedoDepth);
    }

    [Fact]
    public void A_re_announced_identical_value_records_nothing()
    {
        (UndoHistory history, Editable target) = NewTracked();
        target.Text = "same";
        int depth = history.UndoDepth;

        target.Announce(nameof(Editable.Text));

        Assert.Equal(depth, history.UndoDepth);
    }

    [Fact]
    public void A_suppressed_change_still_refreshes_the_recorded_before_value()
    {
        // The regression this guards: if Suppress skipped the value cache as well as the recording, the
        // next genuine edit would record "A -> C" and undo would land on A — a value the user never saw.
        (UndoHistory history, Editable target) = NewTracked();
        target.Text = "A";
        history.Reset();

        using (history.Suppress())
            target.Text = "B";

        target.Text = "C";
        history.UndoCommand.Execute(null);

        Assert.Equal("B", target.Text);
    }

    // ============================ Coalescing ============================

    [Fact]
    public void Consecutive_edits_of_a_coalescing_property_collapse_into_one_step()
    {
        (UndoHistory history, Editable target) = NewTracked();
        foreach (string typed in new[] { "c", "c:", "c:\\", "c:\\temp" })
            target.Text = typed;

        Assert.Equal(1, history.UndoDepth);
        history.UndoCommand.Execute(null);
        Assert.Equal("", target.Text);           // back to where the burst started, not one character
    }

    [Fact]
    public void An_edit_to_another_property_breaks_the_merge_chain()
    {
        (UndoHistory history, Editable target) = NewTracked();
        target.Text = "one";
        target.Flag = true;
        target.Text = "two";

        Assert.Equal(3, history.UndoDepth);
    }

    [Fact]
    public void Break_merge_starts_a_new_step_for_the_same_property()
    {
        (UndoHistory history, Editable target) = NewTracked();
        target.Text = "first";
        history.BreakMerge();
        target.Text = "second";

        Assert.Equal(2, history.UndoDepth);
        history.UndoCommand.Execute(null);
        Assert.Equal("first", target.Text);
    }

    [Fact]
    public void Discrete_values_never_coalesce()
    {
        (UndoHistory history, Editable target) = NewTracked();
        target.Flag = true;
        target.Flag = false;
        target.Flag = true;

        Assert.Equal(3, history.UndoDepth);
    }

    // ============================ Batching ============================

    [Fact]
    public void A_batch_folds_coupled_changes_into_one_step()
    {
        (UndoHistory history, Editable target) = NewTracked();
        using (history.Batch())
        {
            target.Flag = true;
            target.Number = 7;
        }

        Assert.Equal(1, history.UndoDepth);
        history.UndoCommand.Execute(null);
        Assert.False(target.Flag);
        Assert.Equal(0, target.Number);
    }

    [Fact]
    public void Nested_batches_commit_once_at_the_outermost_scope()
    {
        (UndoHistory history, Editable target) = NewTracked();
        using (history.Batch())
        {
            target.Flag = true;
            using (history.Batch())
                target.Number = 3;
            Assert.Equal(0, history.UndoDepth);   // nothing committed while a scope is still open
        }

        Assert.Equal(1, history.UndoDepth);
    }

    [Fact]
    public void An_empty_batch_records_nothing()
    {
        (UndoHistory history, _) = NewTracked();
        using (history.Batch()) { }

        Assert.Equal(0, history.UndoDepth);
        Assert.False(history.IsDirty);            // and does not manufacture unsaved changes
    }

    [Fact]
    public void A_batch_undoes_its_children_in_reverse_order()
    {
        // Order matters for coupled state: the later half has to be unwound before the earlier one, or
        // the earlier one's change hook re-applies it.
        UndoHistory history = new();
        List<string> log = [];
        using (history.Batch())
        {
            history.Record(() => log.Add("undo-first"), () => log.Add("redo-first"));
            history.Record(() => log.Add("undo-second"), () => log.Add("redo-second"));
        }

        history.UndoCommand.Execute(null);
        history.RedoCommand.Execute(null);

        Assert.Equal(new[] { "undo-second", "undo-first", "redo-first", "redo-second" }, log);
    }

    // ============================ Dirty tracking ============================

    [Fact]
    public void A_reset_history_is_clean()
    {
        (UndoHistory history, _) = NewTracked();
        Assert.False(history.IsDirty);
    }

    [Fact]
    public void An_edit_dirties_and_undoing_it_cleans_again()
    {
        (UndoHistory history, Editable target) = NewTracked();
        target.Flag = true;
        Assert.True(history.IsDirty);

        history.UndoCommand.Execute(null);
        Assert.False(history.IsDirty);

        history.RedoCommand.Execute(null);
        Assert.True(history.IsDirty);
    }

    [Fact]
    public void Marking_saved_cleans_without_dropping_the_history()
    {
        (UndoHistory history, Editable target) = NewTracked();
        target.Flag = true;
        history.MarkSaved();

        Assert.False(history.IsDirty);
        Assert.True(history.CanUndo);             // the user can still step back through what they saved

        history.UndoCommand.Execute(null);
        Assert.True(history.IsDirty);             // one step past the save point is unsaved again

        history.RedoCommand.Execute(null);
        Assert.False(history.IsDirty);
    }

    [Fact]
    public void Typing_straight_after_a_save_dirties_even_though_it_would_otherwise_coalesce()
    {
        // The saved position is remembered as the step on top of the stack. If a later keystroke merged
        // into that very step, the stack top would stay reference-equal to the marker and the window
        // would keep claiming it had no unsaved changes.
        (UndoHistory history, Editable target) = NewTracked();
        target.Text = "path";
        history.MarkSaved();

        target.Text = "path2";

        Assert.True(history.IsDirty);
        Assert.Equal(2, history.UndoDepth);
    }

    [Fact]
    public void An_edit_that_discards_the_saved_marker_stays_dirty()
    {
        (UndoHistory history, Editable target) = NewTracked();
        target.Flag = true;
        history.MarkSaved();                      // marker = the Flag change
        history.UndoCommand.Execute(null);        // marker is now in the redo branch
        target.Number = 5;                        // ...which this discards

        Assert.True(history.IsDirty);
        Assert.False(history.CanRedo);
    }

    // ============================ Collections ============================

    private static (UndoHistory History, ObservableCollection<Row> Rows) NewTrackedRows()
    {
        UndoHistory history = new();
        ObservableCollection<Row> rows = [];
        history.TrackCollection(rows);
        history.Reset();
        return (history, rows);
    }

    [Fact]
    public void Undoing_an_added_row_removes_it_and_redo_restores_the_same_instance()
    {
        (UndoHistory history, ObservableCollection<Row> rows) = NewTrackedRows();
        Row added = new() { Name = "a" };
        rows.Add(added);

        history.UndoCommand.Execute(null);
        Assert.Empty(rows);

        history.RedoCommand.Execute(null);
        Assert.Same(added, Assert.Single(rows));
    }

    [Fact]
    public void Undoing_a_removed_row_reinserts_it_at_its_original_index()
    {
        (UndoHistory history, ObservableCollection<Row> rows) = NewTrackedRows();
        Row first = new() { Name = "first" }, middle = new() { Name = "middle" }, last = new() { Name = "last" };
        rows.Add(first);
        rows.Add(middle);
        rows.Add(last);
        history.Reset();

        rows.Remove(middle);
        history.UndoCommand.Execute(null);

        Assert.Equal(new[] { first, middle, last }, rows);
    }

    [Fact]
    public void A_row_restored_by_undo_is_tracked_again()
    {
        // Removal untracks the row so its edits stop being recorded; re-inserting it has to reverse that,
        // or the restored row would be silently unundoable from then on.
        (UndoHistory history, ObservableCollection<Row> rows) = NewTrackedRows();
        Row row = new() { Name = "before" };
        rows.Add(row);
        history.Reset();

        rows.Remove(row);
        history.UndoCommand.Execute(null);
        history.Reset();

        row.Name = "after";
        Assert.Equal(1, history.UndoDepth);
        history.UndoCommand.Execute(null);
        Assert.Equal("before", row.Name);
    }

    [Fact]
    public void Editing_a_row_added_after_tracking_started_is_recorded()
    {
        (UndoHistory history, ObservableCollection<Row> rows) = NewTrackedRows();
        Row row = new() { Name = "one" };
        rows.Add(row);
        history.Reset();

        row.Name = "two";
        history.UndoCommand.Execute(null);

        Assert.Equal("one", row.Name);
    }

    [Fact]
    public void Clearing_a_tracked_collection_undoes_to_its_original_contents()
    {
        // A Reset event carries no old items, so this only works because the tracker keeps a shadow copy.
        (UndoHistory history, ObservableCollection<Row> rows) = NewTrackedRows();
        Row a = new() { Name = "a" }, b = new() { Name = "b" };
        rows.Add(a);
        rows.Add(b);
        history.Reset();

        rows.Clear();
        Assert.Empty(rows);

        history.UndoCommand.Execute(null);
        Assert.Equal(new[] { a, b }, rows);
    }

    [Fact]
    public void Moving_a_row_undoes_back_to_its_original_position()
    {
        (UndoHistory history, ObservableCollection<Row> rows) = NewTrackedRows();
        Row a = new() { Name = "a" }, b = new() { Name = "b" };
        rows.Add(a);
        rows.Add(b);
        history.Reset();

        rows.Move(0, 1);
        Assert.Equal(new[] { b, a }, rows);

        history.UndoCommand.Execute(null);
        Assert.Equal(new[] { a, b }, rows);
    }

    [Fact]
    public void A_collection_edit_made_while_suppressed_is_not_recorded()
    {
        (UndoHistory history, ObservableCollection<Row> rows) = NewTrackedRows();
        using (history.Suppress())
            rows.Add(new Row { Name = "loaded" });

        Assert.False(history.CanUndo);
        Assert.False(history.IsDirty);
    }
}
