using FileManager.Contracts.IPC;
using FileManager.Contracts.Settings;
using FileManager.Core.IPC;
using System.Reflection;
using System.Text.Json.Serialization;

namespace FileManager.Core.Tests.IPC;

/// <summary>Pins the dispatch table to the wire format: DiscriminatorOf must agree with every
/// [JsonDerivedType] on IpcRequest, so a request type added to one but not the other fails here
/// instead of silently answering NOT_IMPLEMENTED.</summary>
public sealed class IpcRequestTypesTests
{
    [Fact]
    public void Dispatch_keys_match_the_JsonDerivedType_table()
    {
        var derivedTypes = typeof(IpcRequest)
            .GetCustomAttributes<JsonDerivedTypeAttribute>()
            .ToList();
        Assert.NotEmpty(derivedTypes);

        foreach (JsonDerivedTypeAttribute derived in derivedTypes)
        {
            IpcRequest instance = Instantiate(derived.DerivedType);
            Assert.Equal((string)derived.TypeDiscriminator!, IpcRequestTypes.DiscriminatorOf(instance));
        }
    }

    private static IpcRequest Instantiate(Type type)
    {
        // Required-member init prevents Activator for some types; construct them explicitly.
        Guid id = Guid.NewGuid();
        return type.Name switch
        {
            nameof(GetStatusRequest) => new GetStatusRequest(),
            nameof(ListProfilesRequest) => new ListProfilesRequest(),
            nameof(GetProfileRequest) => new GetProfileRequest { ProfileId = id },
            nameof(SaveProfileRequest) => new SaveProfileRequest { Profile = null!, AcknowledgeWarnings = false },
            nameof(DeleteProfileRequest) => new DeleteProfileRequest { ProfileId = id },
            nameof(ValidateProfileRequest) => new ValidateProfileRequest { Profile = null! },
            nameof(GetMatchingProfilesRequest) => new GetMatchingProfilesRequest { Path = "x" },
            nameof(RunProfileRequest) => new RunProfileRequest { ProfileId = id, Path = "x" },
            nameof(SetPausedRequest) => new SetPausedRequest { Paused = false },
            nameof(DryRunRequest) => new DryRunRequest { ProfileId = id },
            nameof(DryRunStreamRequest) => new DryRunStreamRequest { ProfileId = id },
            nameof(GetRecentJobsRequest) => new GetRecentJobsRequest(),
            nameof(GetJobLogRequest) => new GetJobLogRequest { JobId = id },
            nameof(SubscribeEventsRequest) => new SubscribeEventsRequest(),
            nameof(GetSettingsRequest) => new GetSettingsRequest(),
            nameof(UpdateSettingsRequest) => new UpdateSettingsRequest { Settings = GlobalSettings.Default },
            nameof(RelocateProfilesRequest) => new RelocateProfilesRequest { NewDirectory = "x" },
            nameof(ShutdownRequest) => new ShutdownRequest(),
            _ => throw new InvalidOperationException(
                $"{type.Name} is in IpcRequest's [JsonDerivedType] table but this test cannot construct it — add a case"),
        };
    }
}
