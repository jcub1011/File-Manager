namespace FileManager.UI.Tests;

/// <summary>Groups the process-heap-measuring memory probes into a collection that xUnit will not run
/// in parallel with any other collection. <see cref="System.GC.GetTotalMemory(bool)"/> is a
/// whole-process reading, so concurrent allocation from unrelated tests would skew the retained-heap
/// deltas these probes assert against a budget.</summary>
[CollectionDefinition("Memory", DisableParallelization = true)]
public sealed class MemoryCollection;
