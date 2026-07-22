using BenchmarkDotNet.Running;

// Discovers every [MemoryDiagnoser]-annotated benchmark class in this assembly.
// Run all:            dotnet run -c Release
// Filter one class:   dotnet run -c Release -- --filter '*IpcFrame*'
// Fast smoke (no rigor): add --job dry
// List without running:  dotnet run -c Release -- --list flat
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);

// Needed so `typeof(Program)` resolves under top-level statements.
internal sealed partial class Program;
