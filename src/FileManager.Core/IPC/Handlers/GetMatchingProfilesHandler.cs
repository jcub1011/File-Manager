using FileManager.Contracts.IPC;
using FileManager.Core.Profiles;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FileManager.Core.IPC.Handlers;

/// <summary>Handles get-matching (§4.9): the active profiles whose sources match a path, for the
/// shell picker and CLI run validation (spec §3.2).</summary>
public sealed class GetMatchingProfilesHandler(IProfileMatcher matcher) : IIpcRequestHandler
{
    public string RequestType => IpcRequestTypes.GetMatching;

    public Task<IpcResponse> HandleAsync(IpcRequest request, CancellationToken ct = default)
    {
        var typed = (GetMatchingProfilesRequest)request;
        IReadOnlyList<ProfileMatch> matches = matcher.FindMatches(typed.Path);
        List<ProfileMatchDto> dtos = new(matches.Count);
        foreach (ProfileMatch match in matches)
            dtos.Add(new ProfileMatchDto(match.ProfileId, match.ProfileName, match.MatchedSourceRoot));
        IpcResponse response = new MatchingProfilesResponse { Matches = dtos };
        return Task.FromResult(response);
    }
}
