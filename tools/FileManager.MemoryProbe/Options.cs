using System;
using System.Collections.Generic;
using System.IO;

namespace FileManager.MemoryProbe;

/// <summary>Parsed command line. Kept separate from <see cref="Program"/> so the defaults and the
/// help text sit next to each other and cannot drift.</summary>
internal sealed record Options
{
    public string TreeRoot { get; init; } = "";
    public int SourceFiles { get; init; } = 33_500;
    public int DestinationFiles { get; init; } = 357_000;
    public int FilesPerDirectory { get; init; } = 200;
    public bool GenerateOnly { get; init; }
    public string? ServiceExePath { get; init; }
    public int SettleSeconds { get; init; } = 30;
    public string? CsvPath { get; init; }
    public int? BudgetPrivateMb { get; init; }
    public string Label { get; init; } = "run";
    public bool Mirror { get; init; } = true;

    public string SourceRoot => Path.Combine(TreeRoot, "source");
    public string DestinationRoot => Path.Combine(TreeRoot, "target");
    public string ManifestPath => Path.Combine(TreeRoot, ".tree-manifest");

    /// <summary>The sentinel that makes regeneration skippable. Any parameter that changes the tree's
    /// SHAPE belongs here — otherwise a cached tree from an earlier shape would be silently reused and
    /// the run would measure the wrong workload.</summary>
    public string ManifestContent => $"v1 source={SourceFiles} dest={DestinationFiles} perDir={FilesPerDirectory}";

    public static Result Parse(string[] args)
    {
        Options options = new();
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            string? Next(string name) =>
                i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{name} needs a value");
            try
            {
                switch (arg)
                {
                    case "--help" or "-h" or "-?":
                        return new Result(null, null);
                    case "--tree":
                        options = options with { TreeRoot = Next(arg)! };
                        break;
                    case "--source-files":
                        options = options with { SourceFiles = int.Parse(Next(arg)!) };
                        break;
                    case "--dest-files":
                        options = options with { DestinationFiles = int.Parse(Next(arg)!) };
                        break;
                    case "--files-per-dir":
                        options = options with { FilesPerDirectory = int.Parse(Next(arg)!) };
                        break;
                    case "--generate-only":
                        options = options with { GenerateOnly = true };
                        break;
                    case "--service":
                        options = options with { ServiceExePath = Next(arg) };
                        break;
                    case "--settle-seconds":
                        options = options with { SettleSeconds = int.Parse(Next(arg)!) };
                        break;
                    case "--csv":
                        options = options with { CsvPath = Next(arg) };
                        break;
                    case "--budget-private-mb":
                        options = options with { BudgetPrivateMb = int.Parse(Next(arg)!) };
                        break;
                    case "--label":
                        options = options with { Label = Next(arg)! };
                        break;
                    case "--additive":
                        options = options with { Mirror = false };
                        break;
                    default:
                        return new Result(null, $"unknown argument \"{arg}\"");
                }
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
            {
                return new Result(null, $"bad value for {arg}: {ex.Message}");
            }
        }

        if (string.IsNullOrWhiteSpace(options.TreeRoot))
            return new Result(null, "--tree is required");
        if (!Path.IsPathFullyQualified(options.TreeRoot))
            return new Result(null, "--tree must be an absolute path");
        if (options.FilesPerDirectory < 1)
            return new Result(null, "--files-per-dir must be at least 1");
        if (!options.GenerateOnly && string.IsNullOrWhiteSpace(options.ServiceExePath))
            return new Result(null, "--service is required unless --generate-only is passed");

        return new Result(options, null);
    }

    internal sealed record Result(Options? Options, string? Error);

    public static IReadOnlyList<string> HelpLines =>
    [
        "FileManager.MemoryProbe — measures the PUBLISHED service's memory across one dry run.",
        "",
        "This is the only thing that measures the real shipped artifact. An in-process xUnit test",
        "cannot: it runs under JIT with the test host's runtimeconfig.json, not the service's",
        "ILC-embedded GC knobs, so every GC-configuration change is invisible to it.",
        "",
        "Usage:",
        "  FileManager.MemoryProbe --tree <dir> [--service <exe>] [options]",
        "",
        "Options:",
        "  --tree <dir>              Where the synthetic source/target trees live. Required.",
        "  --service <exe>           Published FileManager.Service.exe. Required unless --generate-only.",
        "  --source-files <n>        Files under the source root (default 33500).",
        "  --dest-files <n>          Files under the target root (default 357000).",
        "  --files-per-dir <n>       Files per leaf directory (default 200).",
        "  --generate-only           Build/refresh the trees and exit without measuring.",
        "  --settle-seconds <n>      Seconds to wait for the post-run settle sample (default 30).",
        "  --csv <path>              Append the result rows to a CSV so runs are diffable.",
        "  --budget-private-mb <n>   Exit non-zero if settled private memory exceeds this. Makes it a gate.",
        "  --label <text>            Tag for the CSV rows (e.g. a stage name).",
        "  --additive                Preview as AdditiveArchive with destination scanning instead of Mirror.",
        "",
        "Notes:",
        "  * The trees are cached behind a .tree-manifest sentinel; a second run with the same shape",
        "    skips regeneration. Generation is a few minutes on NVMe the first time.",
        "  * Files are 0 bytes. The sweep only stats them, never reads content — but NTFS still costs",
        "    roughly 1 KB per MFT-resident file, so 390,000 files is ~400 MB of disk.",
        "  * Mirror (the default) with an empty survivor set makes EVERY destination file a swept",
        "    orphan. That is the maximum-output case and the one the memory work targets.",
        "  * The service's single-instance mutex is per-user regardless of pipe name, so any already-",
        "    running FileManager.Service must be stopped first, and probe runs must be sequential.",
        "  * --settle-seconds must exceed the trim coordinator's quiet period (25s) or the post-run",
        "    reclaim will not have happened yet and the trim will look broken.",
    ];
}
