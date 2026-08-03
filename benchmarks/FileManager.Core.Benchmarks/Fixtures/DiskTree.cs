namespace FileManager.Core.Benchmarks.Fixtures;

/// <summary>Materializes the on-disk trees the enumeration, sweep and scheduler benchmarks walk.
///
/// <para>Deliberately the same shape as <c>tools/FileManager.MemoryProbe/TreeBuilder.cs</c> — two
/// nesting levels (<c>grp####\dir######</c>), ~64-character names, zero-byte files — so a benchmark
/// number and a probe number describe comparable trees. The manifest cache is the one thing left
/// out: BenchmarkDotNet gives each parameter combination its own <c>[GlobalSetup]</c>, and a cache
/// keyed on shape would be re-validated per combination for no benefit.</para>
///
/// <para>Zero-byte files are correct here, not a shortcut: every path these benchmarks exercise
/// stats entries and reads names, and none reads content. Size would cost disk and generation time
/// without changing what is measured. NTFS still costs roughly 1 KB per MFT-resident file, so a
/// 100,000-file tree is ~100 MB of disk.</para></summary>
internal static class DiskTree
{
    /// <summary>Long enough that the file name dominates the per-entry cost, matching
    /// <see cref="SweepShapes"/>' in-memory shape and the probe's "realistic deep paths".</summary>
    private const int NameLength = 64;

    /// <summary>Creates <paramref name="fileCount"/> zero-byte files under <paramref name="root"/>,
    /// <paramref name="filesPerDirectory"/> to a leaf directory. <paramref name="prefix"/> keeps two
    /// trees built under one benchmark disjoint by name.</summary>
    internal static void Build(string root, int fileCount, int filesPerDirectory, char prefix = 'f')
    {
        int directoryCount = (fileCount + filesPerDirectory - 1) / filesPerDirectory;
        List<int> directories = new(directoryCount);
        for (int d = 0; d < directoryCount; d++)
            directories.Add(d);

        // Parallel over leaf directories: file creation is dominated by per-file MFT work, which the
        // filesystem overlaps well, and each directory is independent. Generation is not measured, so
        // the only goal here is to keep [GlobalSetup] from dominating the run.
        Parallel.ForEach(directories, d =>
        {
            string directory = Path.Combine(root, $"grp{d / 50:D4}", $"dir{d:D6}");
            Directory.CreateDirectory(directory);
            int first = d * filesPerDirectory;
            int last = Math.Min(first + filesPerDirectory, fileCount);
            for (int i = first; i < last; i++)
            {
                using FileStream _ = new(
                    Path.Combine(directory, Name(prefix, i)), FileMode.Create, FileAccess.Write, FileShare.None,
                    bufferSize: 1, FileOptions.None);
            }
        });
    }

    /// <summary>One flat directory of <paramref name="fileCount"/> zero-byte files — the unit of work
    /// for single-level enumeration, which never descends.</summary>
    internal static void BuildFlat(string directory, int fileCount, char prefix = 'f')
    {
        Directory.CreateDirectory(directory);
        for (int i = 0; i < fileCount; i++)
        {
            using FileStream _ = new(
                Path.Combine(directory, Name(prefix, i)), FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 1, FileOptions.None);
        }
    }

    internal static void Delete(string root)
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort temp cleanup; never fail a run over it.
        }
    }

    private static string Name(char prefix, int index) =>
        prefix + index.ToString("D8") + new string('n', NameLength - 13) + ".dat";
}
