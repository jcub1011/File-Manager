using FileManager.Contracts.Profiles;

namespace FileManager.UI.ViewModels;

/// <summary>What a run does to a source file once its copies have landed, in words.
///
/// <para>Shared because it has to be said in two places that must not drift: the import dialog, which
/// describes a profile someone else authored, and the Preview tab's approval footer, which describes the
/// run the user is about to start. The footer went without it for a while — the modal confirmation that
/// used to state it was replaced by the footer, and the footer stated only the copy and target-delete
/// counts. A user with <see cref="OnSuccessAction.PermanentDelete"/> could read "1,204 file(s) to copy or
/// update", press Approve, and lose all 1,204 originals with nothing on screen having said so.</para></summary>
internal static class SourceDisposition
{
    /// <summary>A sentence naming what happens to the source files, and whether that destroys them.
    /// <para><paramref name="destroys"/> is what earns the danger styling: moving to the Recycle Bin or
    /// deleting outright is the only part of a run that costs the user something they still had. Moving
    /// to an archive folder relocates the file and keeps it, so it is stated but not shouted.</para></summary>
    /// <remarks>Takes the two policy values rather than a <see cref="PolicySettings"/> so the editor can
    /// call it from its live fields, without building a whole draft profile to describe one setting.</remarks>
    public static (string Text, bool Destroys) Describe(OnSuccessAction onSuccess, string? archiveFolder)
    {
        // Worded to read correctly in both callers, which is why neither says "above" or "this dialog".
        string text = onSuccess switch
        {
            OnSuccessAction.KeepSource => "Source files are kept in place.",
            OnSuccessAction.MoveToTrash =>
                "Every source file copied is then moved to the RECYCLE BIN.",
            OnSuccessAction.PermanentDelete =>
                "Every source file copied is then PERMANENTLY DELETED — not to the Recycle Bin.",
            OnSuccessAction.MoveToArchive =>
                $"Every source file copied is then moved to the archive folder \"{archiveFolder}\".",
            _ => onSuccess.ToString(),
        };
        return (text, onSuccess is OnSuccessAction.MoveToTrash or OnSuccessAction.PermanentDelete);
    }
}
