using FileManager.Contracts.IPC;
using FileManager.Contracts.Primitives;
using FileManager.Core.Profiles;
using Microsoft.Extensions.Logging;

namespace FileManager.Core.IPC.Handlers;

/// <summary>The one rule for "the profiles on disk changed, so reload the catalog" — used by every
/// handler that mutates the profile store. <see cref="IProfileCatalog.Reload"/> returns early on
/// failure WITHOUT swapping its snapshot and WITHOUT notifying subscribers, so a swallowed failure
/// leaves the engine serving the pre-mutation profile set while the client is told it succeeded: a
/// manual run then resolves the stale profile from <c>catalog.All</c> and executes it under the old
/// policies, up to and including a stale <c>OnSuccessAction.PermanentDelete</c>, and no
/// <c>ProfilesChangedEvent</c> reaches any other client. This lived in three handlers with three
/// different answers (two logged a warning and reported OK; only relocate failed loud) — one rule,
/// one place.
/// <para>Not for the startup load in <c>EngineHost</c>: that one has no client to answer and is
/// deliberately non-fatal.</para></summary>
internal static class CatalogReloadGuard
{
    /// <summary>Reloads the catalog. Returns <c>null</c> when it succeeded; otherwise the
    /// <see cref="ErrorResponse"/> the handler must return instead of its success response.</summary>
    /// <param name="code">The handler's existing failure code — the client displays the message, not
    /// the code, so reuse rather than invent.</param>
    /// <param name="didWhat">Past-tense description of the mutation that already happened on disk
    /// ("saved", "deleted"), so the message tells the user what state they are actually in.</param>
    public static ErrorResponse? Check(IProfileCatalog catalog, ILogger logger, string code, string didWhat)
    {
        Result reload = catalog.Reload();
        if (!reload.TryGetError(out string? error))
            return null;

        logger.LogError("Catalog reload after the profile was {DidWhat} failed: {Error}", didWhat, error);
        return new ErrorResponse
        {
            Code = code,
            Message = $"the profile was {didWhat}, but reloading the profile list failed: {error}; retry, or restart the service",
        };
    }
}
