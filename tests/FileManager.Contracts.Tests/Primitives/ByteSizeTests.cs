using FileManager.Contracts.Primitives;

namespace FileManager.Contracts.Tests.Primitives;

public sealed class ByteSizeTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(1048576, "1 MB")]
    [InlineData(10 * 1048576, "10 MB")]
    [InlineData(1073741824, "1 GB")]
    [InlineData(620L * 1073741824, "620 GB")]
    [InlineData(2L * 1024 * 1024 * 1024 * 1024, "2 TB")]
    public void Formats_positive_sizes_with_adaptive_precision(long bytes, string expected) =>
        Assert.Equal(expected, ByteSize.Format(bytes));

    [Theory]
    [InlineData(-2048, "-2 KB")]
    [InlineData(-1048576, "-1 MB")]
    public void Formats_negative_sizes_with_a_leading_minus(long bytes, string expected) =>
        Assert.Equal(expected, ByteSize.Format(bytes));

    [Fact]
    public void Handles_long_min_value_without_overflow()
    {
        string formatted = ByteSize.Format(long.MinValue);
        Assert.StartsWith("-", formatted);
    }
}
