using FileManager.Core.Journal;
using System.Text;

namespace FileManager.Core.Tests.Journal;

/// <summary>The byte-level line splitter both audit trails and the run snapshot reader now share.
///
/// <para>Worth its own tests because the sharing is the point: a bug here is a bug in the deletion audit
/// trail, the disposition audit trail, and the frozen work list a Mirror run deletes from. Its predecessor
/// in the snapshot reader decoded every line to a string and re-encoded it, which was slower but tolerant
/// of line endings by accident — so tolerance has to be deliberate here.</para></summary>
public sealed class NdjsonLinesTests
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fm-ndjson-" + Guid.NewGuid().ToString("N"));

    private string WriteBytes(string name, string content)
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, name);
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(content));
        return path;
    }

    private static List<string> Text(IEnumerable<byte[]> lines) =>
        [.. lines.Select(l => Encoding.UTF8.GetString(l))];

    [Fact]
    public void Lines_are_split_on_LF_and_empty_lines_are_dropped()
    {
        string path = WriteBytes("lf.ndjsonl", "one\ntwo\n\nthree\n");

        Assert.Equal(["one", "two", "three"], Text(NdjsonLines.ReadAllLines(path)));
    }

    [Fact]
    public void A_final_line_with_no_newline_is_still_read()
    {
        // An append-only file interrupted by a crash mid-write looks exactly like this, and the last
        // record is often the one that matters most.
        string path = WriteBytes("partial.ndjsonl", "one\ntwo");

        Assert.Equal(["one", "two"], Text(NdjsonLines.ReadAllLines(path)));
    }

    [Fact]
    public void CRLF_terminated_lines_read_the_same_as_LF_terminated_ones()
    {
        // These files are written with bare LF, but anything that rewrites one through a text API on
        // Windows re-terminates every line with CRLF. Left on, the CR travels into the framed payload and
        // fails its CRC — so the file reads back EMPTY, which for a delete list reads as "no orphans"
        // rather than as damage.
        string path = WriteBytes("crlf.ndjsonl", "one\r\ntwo\r\n");

        Assert.Equal(["one", "two"], Text(NdjsonLines.ReadAllLines(path)));
    }

    [Fact]
    public void A_line_longer_than_the_read_buffer_is_returned_WHOLE()
    {
        // Split into buffer-sized fragments it would fail its checksum and be reported as a corrupt
        // record — a silent data loss dressed up as detected damage.
        string huge = new('x', 40_000);
        string path = WriteBytes("huge.ndjsonl", $"small\n{huge}\nsmall2\n");

        List<string> lines = Text(NdjsonLines.ReadAllLines(path, bufferSize: 1024));

        Assert.Equal(["small", huge, "small2"], lines);
    }

    [Fact]
    public void A_line_spanning_a_buffer_boundary_is_reassembled()
    {
        string path = WriteBytes("boundary.ndjsonl", string.Concat(
            Enumerable.Range(0, 200).Select(i => new string((char)('a' + i % 26), 100) + "\n")));

        List<string> lines = Text(NdjsonLines.ReadAllLines(path, bufferSize: 256));

        Assert.Equal(200, lines.Count);
        Assert.All(lines, l => Assert.Equal(100, l.Length));
        Assert.All(lines, l => Assert.Equal(l[0], l[^1]));   // never spliced from two different lines
    }

    [Fact]
    public void An_empty_file_yields_nothing()
    {
        string path = WriteBytes("empty.ndjsonl", "");

        Assert.Empty(NdjsonLines.ReadAllLines(path));
    }

    [Fact]
    public void A_tail_read_starts_at_the_first_COMPLETE_line()
    {
        // The bound exists because a monthly audit file is never auto-deleted; the partial first line a
        // tail read lands in must be dropped, not parsed as a record.
        string path = WriteBytes("tail.ndjsonl", "aaaa\nbbbb\ncccc\ndddd\n");

        byte[] bytes = NdjsonLines.ReadTail(path, maxTailBytes: 12, out int start);
        List<string> lines = [.. NdjsonLines.Spans(bytes, start)
            .Select(s => Encoding.UTF8.GetString(bytes, s.Offset, s.Length))];

        // The last 12 bytes are "b\ncccc\ndddd\n"; the leading fragment of "bbbb" goes.
        Assert.Equal(["cccc", "dddd"], lines);
    }

    [Fact]
    public void A_file_smaller_than_the_tail_bound_is_read_whole()
    {
        string path = WriteBytes("small.ndjsonl", "aaaa\nbbbb\n");

        byte[] bytes = NdjsonLines.ReadTail(path, maxTailBytes: 1 << 20, out int start);

        Assert.Equal(0, start);
        Assert.Equal(
            ["aaaa", "bbbb"],
            NdjsonLines.Spans(bytes, start).Select(s => Encoding.UTF8.GetString(bytes, s.Offset, s.Length)));
    }
}
