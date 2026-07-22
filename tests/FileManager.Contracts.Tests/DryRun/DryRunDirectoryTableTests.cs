using FileManager.Contracts.DryRun;

namespace FileManager.Contracts.Tests.DryRun;

/// <summary>Pins the directory-table wire invariants: entries appear once, ancestors first, roots
/// stored whole, and <see cref="DryRunDirectoryTable.Materialize"/> reproduces the original absolute
/// paths byte-for-byte.</summary>
public sealed class DryRunDirectoryTableTests
{
    [Fact]
    public void GetOrAdd_splits_a_chain_ancestors_first_and_round_trips()
    {
        DryRunDirectoryTableBuilder builder = new();

        int index = builder.GetOrAdd(@"C:\a\b\c");

        Assert.Equal(3, index);
        Assert.Equal(4, builder.Count);
        Assert.Equal(new DryRunDirectory(@"C:\", -1), builder.Entries[0]);
        Assert.Equal(new DryRunDirectory("a", 0), builder.Entries[1]);
        Assert.Equal(new DryRunDirectory("b", 1), builder.Entries[2]);
        Assert.Equal(new DryRunDirectory("c", 2), builder.Entries[3]);

        string[] paths = DryRunDirectoryTable.Materialize(builder.Entries);
        Assert.Equal(@"C:\a\b\c", paths[index]);
    }

    [Fact]
    public void GetOrAdd_returns_the_existing_index_for_a_repeat_and_shares_ancestors()
    {
        DryRunDirectoryTableBuilder builder = new();

        int first = builder.GetOrAdd(@"C:\a\b");
        int again = builder.GetOrAdd(@"C:\a\b");
        int sibling = builder.GetOrAdd(@"C:\a\x");

        Assert.Equal(first, again);
        Assert.Equal(4, builder.Count);                         // C:\, a, b, x — the chain is shared
        Assert.Equal(builder.Entries[first].ParentIndex, builder.Entries[sibling].ParentIndex);
    }

    [Fact]
    public void GetOrAdd_ignores_a_trailing_separator_but_keeps_the_drive_root_form()
    {
        DryRunDirectoryTableBuilder builder = new();

        Assert.Equal(builder.GetOrAdd(@"C:\src"), builder.GetOrAdd(@"C:\src\"));
        Assert.Equal(2, builder.Count);

        string[] paths = DryRunDirectoryTable.Materialize(builder.Entries);
        Assert.Equal(@"C:\", paths[0]);
        Assert.Equal(@"C:\src", paths[1]);
    }

    [Fact]
    public void Unc_roots_are_stored_whole_and_round_trip()
    {
        DryRunDirectoryTableBuilder builder = new();

        int index = builder.GetOrAdd(@"\\server\share\team\docs");

        string[] paths = DryRunDirectoryTable.Materialize(builder.Entries);
        Assert.Equal(@"\\server\share\team\docs", paths[index]);
        // The share is the root entry: its whole \\server\share form in one entry, ParentIndex -1.
        Assert.Contains(builder.Entries, e => e.ParentIndex == -1 && e.Name.StartsWith(@"\\", StringComparison.Ordinal));
    }

    [Fact]
    public void Casing_is_preserved_per_distinct_form()
    {
        DryRunDirectoryTableBuilder builder = new();

        int lower = builder.GetOrAdd(@"C:\data\logs");
        int upper = builder.GetOrAdd(@"C:\data\LOGS");

        // Ordinal dedup: distinct casings are distinct entries so displayed paths never change form.
        Assert.NotEqual(lower, upper);
        string[] paths = DryRunDirectoryTable.Materialize(builder.Entries);
        Assert.Equal(@"C:\data\logs", paths[lower]);
        Assert.Equal(@"C:\data\LOGS", paths[upper]);
    }

    [Fact]
    public void Convert_splits_a_file_path_and_its_root_into_the_wire_triple()
    {
        DryRunDirectoryTableBuilder builder = new();

        (int dirIndex, string fileName, int rootDirIndex) = builder.Convert(@"C:\src\photos\a.jpg", @"C:\src");

        Assert.Equal("a.jpg", fileName);
        string[] paths = DryRunDirectoryTable.Materialize(builder.Entries);
        Assert.Equal(@"C:\src\photos", paths[dirIndex]);
        Assert.Equal(@"C:\src", paths[rootDirIndex]);
    }

    [Fact]
    public void Convert_projects_contract_records_field_for_field()
    {
        DryRunDirectoryTableBuilder builder = new();
        PhysicalFile file = new()
        {
            Path = @"C:\a\one.txt",
            Root = @"C:\a",
            Length = 42,
            LastWritten = DateTimeOffset.UnixEpoch,
            IsReparsePoint = true,
        };
        VirtualFileOperation op = new()
        {
            Path = @"C:\t\one (1).txt",
            Root = @"C:\t",
            Kind = OperationKind.Rename,
            SourceIndex = 3,
            SubjectIndex = 7,
            SourceDisposition = Profiles.OnSuccessAction.MoveToTrash,
            Detail = "renamed to avoid a conflict",
        };

        DryRunFile wireFile = builder.Convert(file);
        DryRunOperation wireOp = builder.Convert(op);

        string[] paths = DryRunDirectoryTable.Materialize(builder.Entries);
        Assert.Equal(file.Path, System.IO.Path.Join(paths[wireFile.DirIndex], wireFile.FileName));
        Assert.Equal(file.Root, paths[wireFile.RootDirIndex]);
        Assert.Equal(42, wireFile.Length);
        Assert.Equal(DateTimeOffset.UnixEpoch, wireFile.LastWritten);
        Assert.True(wireFile.IsReparsePoint);

        Assert.Equal(op.Path, System.IO.Path.Join(paths[wireOp.DirIndex], wireOp.FileName));
        Assert.Equal(op.Root, paths[wireOp.RootDirIndex]);
        Assert.Equal(OperationKind.Rename, wireOp.Kind);
        Assert.Equal(3, wireOp.SourceIndex);
        Assert.Equal(7, wireOp.SubjectIndex);
        Assert.Equal(Profiles.OnSuccessAction.MoveToTrash, wireOp.SourceDisposition);
        Assert.Equal("renamed to avoid a conflict", wireOp.Detail);
    }

    [Fact]
    public void FlushNew_slices_exactly_the_entries_added_since_the_last_flush()
    {
        DryRunDirectoryTableBuilder builder = new();

        builder.GetOrAdd(@"C:\a\b");
        IReadOnlyList<DryRunDirectory> firstChunk = builder.FlushNew();
        Assert.Equal(3, firstChunk.Count);                       // C:\, a, b

        builder.GetOrAdd(@"C:\a\b");                             // repeat — nothing new
        builder.GetOrAdd(@"C:\a\c");                             // one new sibling
        IReadOnlyList<DryRunDirectory> secondChunk = builder.FlushNew();
        DryRunDirectory added = Assert.Single(secondChunk);
        Assert.Equal("c", added.Name);

        Assert.Empty(builder.FlushNew());                        // flushing with nothing new is empty

        // Concatenating chunk slices reproduces the full table — the client's assembly contract.
        List<DryRunDirectory> assembled = [.. firstChunk, .. secondChunk];
        Assert.Equal(builder.Entries, assembled);
    }

    [Fact]
    public void RollbackTo_removes_tail_entries_and_forgets_their_paths()
    {
        DryRunDirectoryTableBuilder builder = new();
        builder.GetOrAdd(@"C:\keep");
        int mark = builder.Mark();

        builder.GetOrAdd(@"C:\keep\rejected\deep");
        builder.RollbackTo(mark);

        Assert.Equal(mark, builder.Count);
        // The rolled-back dirs are genuinely forgotten: re-adding assigns fresh indices cleanly.
        int reAdded = builder.GetOrAdd(@"C:\keep\rejected");
        Assert.Equal(mark, reAdded);
        Assert.Equal(@"C:\keep\rejected", DryRunDirectoryTable.Materialize(builder.Entries)[reAdded]);
    }

    [Fact]
    public void RollbackTo_throws_once_the_tail_was_flushed()
    {
        DryRunDirectoryTableBuilder builder = new();
        int mark = builder.Mark();
        builder.GetOrAdd(@"C:\a");
        builder.FlushNew();

        Assert.Throws<InvalidOperationException>(() => builder.RollbackTo(mark));
    }

    [Fact]
    public void Materialize_throws_on_a_forward_or_self_parent_reference()
    {
        Assert.Throws<InvalidOperationException>(() =>
            DryRunDirectoryTable.Materialize([new DryRunDirectory("a", 0)]));
        Assert.Throws<InvalidOperationException>(() =>
            DryRunDirectoryTable.Materialize([new DryRunDirectory(@"C:\", -1), new DryRunDirectory("a", 2)]));
    }

    [Fact]
    public void Materialize_throws_on_an_invalid_negative_parent_reference()
    {
        // Any negative ParentIndex other than -1 (the "root" sentinel) is malformed.
        Assert.Throws<InvalidOperationException>(() =>
            DryRunDirectoryTable.Materialize([new DryRunDirectory("a", -2)]));
    }

    [Fact]
    public void FindInvalidReference_returns_null_when_every_reference_is_in_range()
    {
        // Two directories (indices 0, 1); every file/op index sits within [0, 2).
        Assert.Null(DryRunDirectoryTable.FindInvalidReference(
            directoryCount: 2,
            files: [File(dirIndex: 0, rootDirIndex: 1)],
            operations: [Op(dirIndex: 1, rootDirIndex: 0)]));
    }

    [Theory]
    [InlineData(2, 0)]   // DirIndex == count (one past the end)
    [InlineData(99, 0)]  // DirIndex well past the end
    [InlineData(-1, 0)]  // negative DirIndex
    [InlineData(0, 2)]   // RootDirIndex out of range
    [InlineData(0, -1)]  // negative RootDirIndex
    public void FindInvalidReference_flags_an_out_of_range_file_index(int dirIndex, int rootDirIndex)
    {
        string? bad = DryRunDirectoryTable.FindInvalidReference(
            directoryCount: 2,
            files: [File(dirIndex, rootDirIndex)],
            operations: []);

        Assert.NotNull(bad);
        Assert.Contains("file", bad);
    }

    [Theory]
    [InlineData(2, 0)]
    [InlineData(-1, 0)]
    [InlineData(0, 5)]
    public void FindInvalidReference_flags_an_out_of_range_operation_index(int dirIndex, int rootDirIndex)
    {
        string? bad = DryRunDirectoryTable.FindInvalidReference(
            directoryCount: 2,
            files: [],
            operations: [Op(dirIndex, rootDirIndex)]);

        Assert.NotNull(bad);
        Assert.Contains("operation", bad);
    }

    private static DryRunFile File(int dirIndex, int rootDirIndex) => new()
    {
        DirIndex = dirIndex,
        FileName = "f.txt",
        RootDirIndex = rootDirIndex,
        Length = 0,
        LastWritten = default,
    };

    private static DryRunOperation Op(int dirIndex, int rootDirIndex) => new()
    {
        DirIndex = dirIndex,
        FileName = "f.txt",
        RootDirIndex = rootDirIndex,
        Kind = OperationKind.New,
    };
}
