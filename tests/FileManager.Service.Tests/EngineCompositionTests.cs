using FileManager.Core;
using FileManager.Core.IPC;
using FileManager.Core.Jobs;
using FileManager.Core.Journal;
using FileManager.Service;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Reflection;

namespace FileManager.Service.Tests;

/// <summary>The guard the repo did not have: the service's object graph and its IPC dispatch table are
/// now built by a test. Before this, a missing registration or a handler absent from the table failed
/// only at runtime in production — as NOT_IMPLEMENTED to the UI, not as a build break.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class EngineCompositionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fm-composition-" + Guid.NewGuid().ToString("N"));

    public EngineCompositionTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    /// <summary>EngineHost is registered as a hosted service and takes IHostApplicationLifetime, which a
    /// bare ServiceCollection does not provide. Nothing calls it here — the graph is only built.</summary>
    private sealed class StubLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    private ServiceProvider BuildProvider()
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton<IHostApplicationLifetime, StubLifetime>();
        EngineComposition.AddEngine(services, new EnginePaths { Root = Path.Combine(_root, "engine") });
        // ValidateOnBuild turns an unsatisfiable dependency into a failure here rather than at the first
        // request that needs it; ValidateScopes catches a singleton capturing a scoped service.
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }

    [Fact]
    public void Every_registered_service_resolves()
    {
        using ServiceProvider provider = BuildProvider();

        Assert.NotNull(provider.GetRequiredService<IIpcServer>());
        Assert.NotNull(provider.GetRequiredService<IJobOrchestrator>());
        Assert.NotNull(provider.GetRequiredService<ICrashRecovery>());
        Assert.NotNull(provider.GetRequiredService<IReadOnlyDictionary<string, IIpcRequestHandler>>());
    }

    [Fact]
    public void The_dispatch_table_covers_every_wire_discriminator()
    {
        // The third leg of the discriminator contract. IpcRequestTypesTests pins the other two
        // (IpcRequest's [JsonDerivedType] attributes against these consts); nothing saw this list at
        // all, and a discriminator missing from it answers NOT_IMPLEMENTED at runtime.
        using ServiceProvider provider = BuildProvider();
        IReadOnlyDictionary<string, IIpcRequestHandler> table =
            provider.GetRequiredService<IReadOnlyDictionary<string, IIpcRequestHandler>>();

        HashSet<string> expected = typeof(IpcRequestTypes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            // Subscribe is served inside IpcServer (it upgrades the connection to an event stream), so
            // it deliberately has no entry in the handler table.
            .Where(d => d != IpcRequestTypes.Subscribe)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(expected.OrderBy(d => d, StringComparer.Ordinal),
                     table.Keys.OrderBy(d => d, StringComparer.Ordinal));
        // The set equality above is the real assertion; this is the belt-and-braces count, which must
        // move deliberately whenever a request type is added.
        Assert.Equal(20, table.Count);
    }

    [Fact]
    public void The_streaming_dry_run_handler_is_in_the_table_and_is_streaming()
    {
        // The handler the UI uses for EVERY dry run, and the one the smoke test's hand-built copy of
        // this graph had silently lost.
        using ServiceProvider provider = BuildProvider();
        IReadOnlyDictionary<string, IIpcRequestHandler> table =
            provider.GetRequiredService<IReadOnlyDictionary<string, IIpcRequestHandler>>();

        Assert.True(table.TryGetValue(IpcRequestTypes.DryRunStream, out IIpcRequestHandler? handler));
        // IpcServer routes on this type test, not on the discriminator.
        Assert.IsAssignableFrom<IIpcStreamingRequestHandler>(handler);
    }

    [Fact]
    public void Every_handler_in_the_table_is_keyed_by_its_own_RequestType()
    {
        using ServiceProvider provider = BuildProvider();
        IReadOnlyDictionary<string, IIpcRequestHandler> table =
            provider.GetRequiredService<IReadOnlyDictionary<string, IIpcRequestHandler>>();

        foreach (KeyValuePair<string, IIpcRequestHandler> entry in table)
            Assert.Equal(entry.Value.RequestType, entry.Key);
    }
}
