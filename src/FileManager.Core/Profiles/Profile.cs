using System;
using System.Collections.Generic;
using System.Text;

namespace FileManager.Core.Profiles;

public sealed record Profile
{
    /// <summary>
    /// The version of this schema.
    /// </summary>
    public required int SchemaVersion { get; init; }

    /// <summary>
    /// The unique identifier of this profile.
    /// </summary>
    public required Guid Id { get; init; }

    /// <summary>
    /// The name of this profile.
    /// </summary>
    public required string Name { get; init; }
}
