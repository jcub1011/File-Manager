using FileManager.Core;
using FileManager.Core.DryRun;
using FileManager.Core.Files;
using FileManager.Core.Filtering;
using FileManager.Core.IPC;
using FileManager.Core.IPC.Handlers;
using FileManager.Core.Placement;
using FileManager.Core.Platform;
using FileManager.Core.Profiles;
using FileManager.Core.Watching;
using FileManager.Platform.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System;
using System.Collections.Generic;

namespace FileManager.Service;

// The composition root is the one place that binds Windows implementations to Core's platform
// interfaces; a Linux host would be a sibling composition root, not a branch here (§10.5).
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal static class Program
{
    public static void Main(string[] args)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

        IServiceCollection services = builder.Services;
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(EnginePaths.Default());
        services.AddSingleton<IFileSystemService, FileSystemService>();
        services.AddSingleton<IFilterCompiler, FilterCompiler>();
        services.AddSingleton<IProfileValidator, ProfileValidator>();
        services.AddSingleton<IProfileStore, ProfileStore>();
        services.AddSingleton<IProfileCatalog, ProfileCatalog>();
        services.AddSingleton<ISourceScanner, SourceScanner>();
        services.AddSingleton<IFileHasher, FileHasher>();
        services.AddSingleton<IConflictResolver, ConflictResolver>();
        services.AddSingleton<IDryRunEngine, DryRunEngine>();
        services.AddSingleton<IIpcEndpointProvider, WindowsIpcEndpointProvider>();

        // Explicit dispatch table — no reflection-based handler discovery (§1 AOT constraints).
        services.AddSingleton<GetStatusHandler>();
        services.AddSingleton<ListProfilesHandler>();
        services.AddSingleton<GetProfileHandler>();
        services.AddSingleton<SaveProfileHandler>();
        services.AddSingleton<DeleteProfileHandler>();
        services.AddSingleton<ValidateProfileHandler>();
        services.AddSingleton<DryRunHandler>();
        services.AddSingleton<IReadOnlyDictionary<string, IIpcRequestHandler>>(provider =>
        {
            IIpcRequestHandler[] handlers =
            [
                provider.GetRequiredService<GetStatusHandler>(),
                provider.GetRequiredService<ListProfilesHandler>(),
                provider.GetRequiredService<GetProfileHandler>(),
                provider.GetRequiredService<SaveProfileHandler>(),
                provider.GetRequiredService<DeleteProfileHandler>(),
                provider.GetRequiredService<ValidateProfileHandler>(),
                provider.GetRequiredService<DryRunHandler>(),
            ];
            Dictionary<string, IIpcRequestHandler> table = new(StringComparer.Ordinal);
            foreach (IIpcRequestHandler handler in handlers)
                table.Add(handler.RequestType, handler);
            return table;
        });
        services.AddSingleton<IIpcServer, IpcServer>();

        services.AddHostedService<EngineHost>();

        builder.Build().Run();
    }
}
