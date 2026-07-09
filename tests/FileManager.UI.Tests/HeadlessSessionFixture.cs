using Avalonia.Headless;
using Xunit;

namespace FileManager.UI.Tests;

/// <summary>One <see cref="HeadlessUnitTestSession"/> shared by every headless smoke test in the
/// assembly. Avalonia's headless session owns a process-global UI thread; creating more than one per
/// process deadlocks (a Dispatch on a second session hangs), so all smoke tests must dispatch through
/// this single shared instance rather than each calling StartNew.</summary>
public sealed class HeadlessSessionFixture : IDisposable
{
    public HeadlessUnitTestSession Session { get; } = HeadlessUnitTestSession.StartNew(typeof(TestAppBuilder));

    public void Dispose() => Session.Dispose();
}

[CollectionDefinition(Name)]
public sealed class HeadlessCollection : ICollectionFixture<HeadlessSessionFixture>
{
    public const string Name = "Headless";
}
