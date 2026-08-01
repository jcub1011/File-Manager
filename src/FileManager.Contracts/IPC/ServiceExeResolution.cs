using System.Collections.Generic;

namespace FileManager.Contracts.IPC;

/// <summary>Where a candidate service-executable path came from. The declaration order IS the
/// precedence order.</summary>
public enum ServiceExeSource
{
    /// <summary>The user's "Service executable path" setting. First because it is the only one the
    /// user can see and correct, so an error telling them to fix it in Settings has to be true.</summary>
    Setting,

    /// <summary>The <c>FILEMANAGER_SERVICE_EXE</c> environment variable — an F5-development
    /// affordance for when the UI and the Service publish to different directories.</summary>
    Environment,

    /// <summary>FileManager.Service.exe beside the calling binary: how a normal install is laid out.</summary>
    BesideApp,
}

/// <summary>One place the service executable might be, and whether it is actually there.</summary>
/// <param name="IsUsable">The path is absolute and a file exists at it. A relative path is never
/// usable: it would resolve against the working directory, which in a real launch is Program Files or
/// System32.</param>
public sealed record ServiceExeCandidate(ServiceExeSource Source, string Path, bool IsUsable);

/// <summary>Every place the service executable was looked for, in precedence order, and which one
/// won. Returned by <see cref="ServiceLauncher.Resolve"/> so the settings window can explain the
/// outcome — which path will actually be used, and why the configured one was not — without
/// reimplementing the precedence rules and drifting from them.</summary>
public sealed record ServiceExeResolution(IReadOnlyList<ServiceExeCandidate> Candidates)
{
    /// <summary>The first candidate that is actually there, or null when none is — in which case the
    /// service cannot be started at all.</summary>
    public ServiceExeCandidate? Chosen
    {
        get
        {
            foreach (ServiceExeCandidate candidate in Candidates)
            {
                if (candidate.IsUsable)
                    return candidate;
            }
            return null;
        }
    }

    /// <summary>The candidate from the user's setting, or null when the setting is blank.</summary>
    public ServiceExeCandidate? Configured
    {
        get
        {
            foreach (ServiceExeCandidate candidate in Candidates)
            {
                if (candidate.Source == ServiceExeSource.Setting)
                    return candidate;
            }
            return null;
        }
    }

    /// <summary>True when the user set a path that is not there and something else will be used
    /// instead. The one case that most needs saying out loud: everything keeps working, so nothing
    /// else would ever tell the user their setting is being ignored.</summary>
    public bool FellBack => Configured is { IsUsable: false } && Chosen is not null;
}
