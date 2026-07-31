namespace FileManager.UI.Tests;

/// <summary>The UI publishes with <c>PublishAot</c>, so every binding must be a compiled binding. A
/// <c>{Binding}</c> in an <c>x:CompileBindings="False"</c> region becomes a ReflectionBinding, which the
/// ILC analyser rejects twice over: IL3050 because the binding path needs dynamic code, and IL2026
/// because a member reached only from a markup string is invisible to the trimmer and can be removed —
/// leaving a control that silently does nothing in a published build while working perfectly under a JIT
/// F5 run. Opting out also forfeits the AVLN2000 build error that catches a mistyped or renamed member.
/// <para>Reintroducing the opt-out is occasionally the only way to bind one template against unrelated
/// DataContext types. If that comes up, give them a shared interface and set <c>x:DataType</c> to it —
/// which is what <see cref="ViewModels.IDryRunFileRow"/> exists for — rather than turning compiled
/// bindings off.</para></summary>
public sealed class CompiledBindingContractTests
{
    private static string ProjectDirectory()
    {
        // Walk up from the test binaries to the repo, then into the UI project.
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "FileManager.UI")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src", "FileManager.UI");
    }

    [Fact]
    public void No_markup_opts_out_of_compiled_bindings()
    {
        string[] offenders = Directory
            .EnumerateFiles(ProjectDirectory(), "*.axaml", SearchOption.AllDirectories)
            .Where(f => File.ReadAllText(f).Contains("x:CompileBindings=\"False\"", StringComparison.Ordinal))
            .Select(f => Path.GetFileName(f))
            .ToArray();

        Assert.True(offenders.Length == 0,
            "These files opt out of compiled bindings, which is not AOT-safe (IL3050/IL2026) and gives up " +
            "the AVLN2000 build check: " + string.Join(", ", offenders) +
            ". Set x:DataType to the bound type — or to a shared interface when one template serves " +
            "several — instead of disabling compiled bindings.");
    }

    [Fact]
    public void The_project_still_defaults_to_compiled_bindings()
    {
        // The per-file opt-outs above are only meaningful while the project-wide default is on.
        string csproj = File.ReadAllText(Path.Combine(ProjectDirectory(), "FileManager.UI.csproj"));
        Assert.Contains("<AvaloniaUseCompiledBindingsByDefault>true</AvaloniaUseCompiledBindingsByDefault>", csproj);
    }
}
