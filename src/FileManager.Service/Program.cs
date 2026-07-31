using FileManager.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using System;
using System.IO;

namespace FileManager.Service;

// The process entry point: real log files, process-wide exception hooks, and Run(). The object graph
// itself is in EngineComposition, which a test can build — see EngineCompositionTests.
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal static class Program
{
    public static void Main(string[] args)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

        // File logging (spec §9). Configured in code — no reflection-based appsettings binding
        // (§1 AOT constraints). Rolling daily, 14-day retention: logs\service-YYYYMMDD.log.
        EnginePaths paths = EnginePaths.Default();
        Directory.CreateDirectory(paths.LogsDirectory);
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                Path.Combine(paths.LogsDirectory, "service-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14)
            .CreateLogger();

        // Last-resort safety net: capture crashes that escape the host so they reach the log
        // before the process dies (mirrors the UI's handlers in FileManager.UI/Program.cs).
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Fatal(e.ExceptionObject as Exception, "Unhandled exception in service host (terminating: {IsTerminating})", e.IsTerminating);
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error(e.Exception, "Unobserved task exception in service host");
            e.SetObserved();
        };

        IServiceCollection services = builder.Services;
        builder.Logging.ClearProviders();
        services.AddSerilog();

        // The whole object graph lives in EngineComposition so a test can build and validate it;
        // Program owns only what a test must not do (real log files, process-wide exception hooks).
        EngineComposition.AddEngine(services, paths);

        try
        {
            builder.Build().Run();
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }
}
