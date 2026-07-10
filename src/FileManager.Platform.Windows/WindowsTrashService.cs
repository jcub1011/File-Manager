using FileManager.Contracts.Primitives;
using FileManager.Core.Platform;
using Microsoft.Extensions.Logging;
using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;
using System.Threading;

namespace FileManager.Platform.Windows;

/// <summary>Moves a file to the real Recycle Bin via the Shell <c>IFileOperation</c> COM interface
/// (spec §5.3, §4.8), driven through the source-generated COM marshaller (<see cref="GeneratedComInterfaceAttribute"/>,
/// AOT-safe — §1). The operation runs on a dedicated STA thread (shell COM expects an apartment)
/// with the UI fully suppressed; any COM/apartment fault degrades to a <see cref="Result"/> failure
/// rather than a crash, so a disposition can log and move on.</summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsTrashService(ILogger<WindowsTrashService> logger) : ITrashService
{
    // FOF_SILENT|FOF_NOCONFIRMATION|FOF_NOERRORUI|FOF_NOCONFIRMMKDIR (0x614) + FOF_ALLOWUNDO (0x40)
    // + FOFX_RECYCLEONDELETE (0x00080000) — recycle, never prompt, never permanently delete.
    private const uint OperationFlags = 0x614 | 0x40 | 0x00080000;
    private const uint ClsctxInprocServer = 0x1;

    private static readonly Guid ClsidFileOperation = new("3ad05575-8857-4850-9277-11b85bdb8e09");
    private static readonly Guid IidIUnknown = new("00000000-0000-0000-C000-000000000046");
    private static readonly Guid IidIShellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");

    private static readonly StrategyBasedComWrappers ComWrappers = new();

    public Result MoveToTrash(string absolutePath)
    {
        Result result = Result.Failure("trash operation did not run");
        var thread = new Thread(() => result = RunSta(absolutePath)) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return result;
    }

    private Result RunSta(string absolutePath)
    {
        int hrInit = CoInitializeEx(IntPtr.Zero, 0x2 /* COINIT_APARTMENTTHREADED */);
        try
        {
            nint operationPtr = 0;
            nint shellItemPtr = 0;
            try
            {
                int hr = CoCreateInstance(in ClsidFileOperation, IntPtr.Zero, ClsctxInprocServer, in IidIUnknown, out operationPtr);
                if (hr < 0 || operationPtr == 0)
                    return $"could not create IFileOperation (hr 0x{hr:X8})";

                var operation = (IFileOperation)ComWrappers.GetOrCreateObjectForComInstance(operationPtr, CreateObjectFlags.None);

                Guid shellItemIid = IidIShellItem;
                hr = SHCreateItemFromParsingName(absolutePath, IntPtr.Zero, in shellItemIid, out shellItemPtr);
                if (hr < 0 || shellItemPtr == 0)
                    return $"could not resolve shell item for \"{absolutePath}\" (hr 0x{hr:X8})";

                var shellItem = (IShellItem)ComWrappers.GetOrCreateObjectForComInstance(shellItemPtr, CreateObjectFlags.None);

                operation.SetOperationFlags(OperationFlags);
                operation.DeleteItem(shellItem, IntPtr.Zero);
                operation.PerformOperations();
                return Result.Success();
            }
            finally
            {
                if (shellItemPtr != 0) Marshal.Release(shellItemPtr);
                if (operationPtr != 0) Marshal.Release(operationPtr);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Moving {Path} to the Recycle Bin failed", absolutePath);
            return $"could not move \"{absolutePath}\" to the Recycle Bin: {ex.Message}";
        }
        finally
        {
            // Balance a successful CoInitializeEx; RPC_E_CHANGED_MODE means someone else owns the apartment.
            if (hrInit >= 0)
                CoUninitialize();
        }
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(IntPtr reserved, uint coInit);

    [LibraryImport("ole32.dll")]
    private static partial void CoUninitialize();

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid clsid, IntPtr outer, uint clsContext, in Guid iid, out nint ppv);

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHCreateItemFromParsingName(string path, IntPtr pbc, in Guid riid, out nint ppv);
}

/// <summary>Opaque shell-item identity — used only as a marshalled parameter to
/// <see cref="IFileOperation.DeleteItem"/>, so no methods are declared.</summary>
[GeneratedComInterface]
[Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
[SupportedOSPlatform("windows")]
internal partial interface IShellItem;

/// <summary>Minimal <c>IFileOperation</c> vtable — slots are declared in exact COM order up to the
/// members we invoke (SetOperationFlags, DeleteItem, PerformOperations); earlier unused slots are
/// placeholders that preserve vtable layout and are never called.</summary>
[GeneratedComInterface]
[Guid("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8")]
[SupportedOSPlatform("windows")]
internal partial interface IFileOperation
{
    void Advise(nint pfops, out uint pdwCookie);          // 1
    void Unadvise(uint dwCookie);                          // 2
    void SetOperationFlags(uint dwOperationFlags);         // 3
    void SetProgressMessage(nint pszMessage);              // 4
    void SetProgressDialog(nint popd);                     // 5
    void SetProperties(nint pproparray);                   // 6
    void SetOwnerWindow(nint hwndOwner);                   // 7
    void ApplyPropertiesToItem(nint psiItem);              // 8
    void ApplyPropertiesToItems(nint punkItems);           // 9
    void RenameItem(nint psiItem, nint pszNewName, nint pfopsProgressSink);   // 10
    void RenameItems(nint pUnkItems, nint pszNewName);     // 11
    void MoveItem(nint psiItem, nint psiDestinationFolder, nint pszNewName, nint pfopsProgressSink);   // 12
    void MoveItems(nint punkItems, nint psiDestinationFolder);   // 13
    void CopyItem(nint psiItem, nint psiDestinationFolder, nint pszCopyName, nint pfopsProgressSink);  // 14
    void CopyItems(nint punkItems, nint psiDestinationFolder);   // 15
    void DeleteItem(IShellItem psiItem, nint pfopsProgressSink); // 16
    void DeleteItems(nint punkItems);                      // 17
    void NewItem(nint psiDestinationFolder, uint dwFileAttributes, nint pszName, nint pszTemplateName, nint pfopsProgressSink);  // 18
    void PerformOperations();                              // 19
}
