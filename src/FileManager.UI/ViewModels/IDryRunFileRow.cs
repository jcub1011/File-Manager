using CommunityToolkit.Mvvm.Input;

namespace FileManager.UI.ViewModels;

/// <summary>The row-level file menu contract, implemented by every dry-run row type the shared
/// <c>FileContextMenu</c> flyout is applied to (<see cref="DryRunFileRow"/>,
/// <see cref="DryRunDestinationRow"/>) plus <see cref="DryRunTreeNode"/>, whose node menu binds the
/// same names.
/// <para>Why it exists: that flyout lives in an <c>x:CompileBindings="False"</c> region of
/// DryRunView.axaml — one flyout serves unrelated DataContext types, so it has to — which means every
/// one of these five bindings is resolved by reflection at runtime. Renaming or removing a member on
/// one of the three types (including indirectly, by renaming a <c>[RelayCommand]</c> method on the tree
/// node, which changes the generated property name) used to compile cleanly, pass the XAML compile, and
/// produce a dead right-click menu item for exactly one row kind at runtime. The only thing holding
/// them in step was a comment on each type telling the reader to mirror the others. This interface is
/// the compile-time guarantee of what the markup assumes.</para>
/// <para>Members stay declared ON the concrete types — the flyouts bind by reflection against the
/// runtime type, where default interface members are not visible. <c>OpenFolderInExplorerCommand</c> is
/// deliberately absent: it exists only on the tree node, which is a genuine domain difference.</para></summary>
public interface IDryRunFileRow
{
    IRelayCommand CopyPathCommand { get; }
    IRelayCommand CopyNameCommand { get; }
    IRelayCommand CopyNameWithoutExtensionCommand { get; }
    IRelayCommand OpenFileCommand { get; }
    IRelayCommand RevealInExplorerCommand { get; }
}
