using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace FileManager.MemoryProbe;

/// <summary>Builds (and caches) the synthetic trees the probe measures against.</summary>
internal static class TreeBuilder
{
    /// <summary>Long enough that a wire record's file name dominates it, matching the ~90-character
    /// paths the UI memory probe calls the "realistic deep paths" shape. A short-name tree would make
    /// every per-entry cost look better than it is on real data.</summary>
    private const int NameLength = 64;

    public static void EnsureTrees(Options options)
    {
        if (IsCached(options))
        {
            Console.WriteLine($"Trees already match {options.ManifestContent} — skipping generation.");
            return;
        }

        // The shape changed (or this is the first run). Clear rather than merge: leftover files from a
        // larger previous shape would silently inflate the sweep and make two runs incomparable.
        if (Directory.Exists(options.TreeRoot))
        {
            Console.WriteLine($"Tree shape changed — clearing {options.TreeRoot} …");
            Directory.Delete(options.TreeRoot, recursive: true);
        }

        Console.WriteLine(
            $"Generating {options.SourceFiles:N0} source + {options.DestinationFiles:N0} destination files " +
            $"({options.FilesPerDirectory} per directory). This takes a few minutes the first time.");

        // Distinct file-name prefixes are load-bearing, not cosmetic. Under PreserveStructure a source
        // file's resulting destination path is its relative path under the target root; if the two
        // trees used the same names, every source file would land on an existing destination file,
        // making those files SURVIVORS and excluding them from the sweep. Different prefixes keep the
        // survivor set disjoint, so all --dest-files entries are swept — the maximum-output case this
        // harness exists to measure.
        Generate(options.SourceRoot, options.SourceFiles, options.FilesPerDirectory, prefix: 's');
        Generate(options.DestinationRoot, options.DestinationFiles, options.FilesPerDirectory, prefix: 'd');
        File.WriteAllText(options.ManifestPath, options.ManifestContent);
        Console.WriteLine("Trees ready.");
    }

    private static bool IsCached(Options options)
    {
        if (!File.Exists(options.ManifestPath))
            return false;
        try
        {
            return File.ReadAllText(options.ManifestPath).Trim() == options.ManifestContent;
        }
        catch (IOException)
        {
            return false;   // unreadable sentinel — regenerate rather than trust it
        }
    }

    private static void Generate(string root, int fileCount, int filesPerDirectory, char prefix)
    {
        int directoryCount = (fileCount + filesPerDirectory - 1) / filesPerDirectory;
        List<int> directories = new(directoryCount);
        for (int d = 0; d < directoryCount; d++)
            directories.Add(d);

        // Parallel over leaf directories: file creation is dominated by per-file MFT work, which the
        // filesystem overlaps well. Each directory is independent, so no coordination is needed.
        Parallel.ForEach(directories, d =>
        {
            // Two nesting levels so the wire contract's directory table has realistic depth to
            // normalize, rather than one flat level that would understate its size.
            string directory = Path.Combine(root, $"grp{d / 50:D4}", $"dir{d:D6}");
            Directory.CreateDirectory(directory);
            int first = d * filesPerDirectory;
            int last = Math.Min(first + filesPerDirectory, fileCount);
            for (int i = first; i < last; i++)
            {
                // Zero-byte: the dry run stats and existence-probes but never reads content, so file
                // size would only cost disk and generation time without changing what is measured.
                using FileStream _ = new(
                    Path.Combine(directory, Name(prefix, i)), FileMode.Create, FileAccess.Write, FileShare.None,
                    bufferSize: 1, FileOptions.None);
            }
        });
    }

    private static string Name(char prefix, int index) =>
        prefix + index.ToString("D8") + new string('n', NameLength - 13) + ".dat";
}
