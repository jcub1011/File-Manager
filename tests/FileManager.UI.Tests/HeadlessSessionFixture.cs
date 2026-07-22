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

public static class HeadlessSessionExtensions
{
    /// <summary>Dispatches an async test body and awaits it to completion. Required for any async
    /// lambda: <c>Session.Dispatch(async () => ...)</c> binds to <c>Dispatch&lt;TResult&gt;(Func&lt;TResult&gt;)</c>
    /// with TResult = Task (the session has no <c>Func&lt;Task&gt;</c> overload), so the dispatcher stops
    /// pumping at the lambda's first real suspension and everything after it — asserts included —
    /// silently never runs inside the test. Wrapping to <c>Func&lt;Task&lt;bool&gt;&gt;</c> hits the overload
    /// that awaits the inner task.</summary>
    public static Task DispatchAsync(
        this HeadlessUnitTestSession session, Func<Task> action, CancellationToken ct) =>
        session.Dispatch(async () => { await action(); return true; }, ct);
}

[CollectionDefinition(Name)]
public sealed class HeadlessCollection : ICollectionFixture<HeadlessSessionFixture>
{
    public const string Name = "Headless";
}
