using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using FileManager.UI.ViewModels;
using System;

namespace FileManager.UI.Views
{
    public partial class MainWindow : Window
    {
        // Close is a two-pass affair: the first OnClosing cancels, runs async teardown (job warning +
        // service shutdown), and only re-invokes Close() once the VM allows it.
        private bool _closeConfirmed;

        // Guards the async teardown so it runs exactly once. OnClosing is async void, so a second
        // close attempt (e.g. a double-click) arriving while RequestCloseAsync is still awaiting would
        // otherwise re-enter with _closeConfirmed still false and fire a second service shutdown.
        private bool _closing;

        // The resizable profile sidebar column (column 0 of the content grid).
        private ColumnDefinition ProfilePanelColumn => ContentGrid.ColumnDefinitions[0];

        public MainWindow()
        {
            InitializeComponent();
            UpdateMaximizeGlyph();

            // Double-click the splitter to toggle the collapsed sidebar. GridSplitter marks pointer
            // input handled for dragging, so DoubleTapped may never fire — subscribe to PointerPressed
            // with handledEventsToo and detect the double via ClickCount.
            SidebarSplitter.AddHandler(PointerPressedEvent, OnSplitterPointerPressed,
                RoutingStrategies.Bubble, handledEventsToo: true);
        }

        // Apply the persisted sidebar layout once the template + DataContext are in place.
        protected override void OnLoaded(RoutedEventArgs e)
        {
            base.OnLoaded(e);
            if (DataContext is MainWindowViewModel vm)
                ApplyCollapseState(vm.SidebarCollapsed, vm);
        }

        private void OnSplitterPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (e.ClickCount == 2 && DataContext is MainWindowViewModel vm)
            {
                bool collapsing = !vm.SidebarCollapsed;
                // Remember the width we're leaving so expanding restores it.
                if (collapsing)
                    vm.SidebarExpandedWidth = ProfilePanelColumn.Width.Value;

                vm.SidebarCollapsed = collapsing;
                ApplyCollapseState(collapsing, vm);
                vm.SaveSidebarState();
                e.Handled = true;
            }
        }

        // Drive the sidebar column geometry from the collapsed flag. Collapsing pins the column to the
        // rail width (Min == Max) so the still-visible splitter's drag is a no-op; expanding restores
        // the resizable range and the last expanded width. Does not itself capture the current width —
        // callers do that before flipping so this stays safe to call on load.
        private void ApplyCollapseState(bool collapsed, MainWindowViewModel vm)
        {
            if (collapsed)
            {
                ProfilePanelColumn.MinWidth = MainWindowViewModel.CollapsedSidebarWidth;
                ProfilePanelColumn.MaxWidth = MainWindowViewModel.CollapsedSidebarWidth;
                ProfilePanelColumn.Width = new GridLength(MainWindowViewModel.CollapsedSidebarWidth);
            }
            else
            {
                ProfilePanelColumn.MinWidth = MainWindowViewModel.MinExpandedSidebarWidth;
                ProfilePanelColumn.MaxWidth = double.PositiveInfinity;
                ProfilePanelColumn.Width = new GridLength(vm.SidebarExpandedWidth);
            }
        }

        // WindowDecorations="None" means we draw the caption buttons, so their actions are wired here
        // rather than by the framework. Close() routes through the two-pass OnClosing teardown below.
        private void OnMinimize(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void OnMaximizeRestore(object? sender, RoutedEventArgs e) =>
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

        private void OnClose(object? sender, RoutedEventArgs e) => Close();

        // Keep the maximize/restore glyph and tooltip in sync with the window state — it also changes
        // when the user double-clicks the title bar (native ElementRole="TitleBar" behaviour).
        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == WindowStateProperty)
                UpdateMaximizeGlyph();
        }

        private void UpdateMaximizeGlyph()
        {
            // Called from the constructor before the named fields are assigned during early property
            // changes; guard until the template has populated them.
            if (MaximizeIcon is null || MaximizeButton is null)
                return;

            bool maximized = WindowState == WindowState.Maximized;
            if (this.TryFindResource(maximized ? "IconRestore" : "IconMaximize", out object? geometry)
                && geometry is Geometry g)
                MaximizeIcon.Data = g;
            ToolTip.SetTip(MaximizeButton, maximized ? "Restore" : "Maximize");
        }

        // NOTE: this override must stay `async void` — Avalonia's OnClosing returns void and the
        // framework does not await it, so `async Task` would not compile as an override and its
        // exceptions would go unobserved. Because an exception escaping an async void crashes the
        // process, the ENTIRE body is wrapped in try/catch; nothing may throw outside it.
        protected override async void OnClosing(WindowClosingEventArgs e)
        {
            try
            {
                // Persist the latest expanded width the user dragged to (the splitter mutates the
                // column directly, so read it back here). Harmless to repeat on the confirmed pass.
                if (DataContext is MainWindowViewModel sidebarVm && !sidebarVm.SidebarCollapsed)
                {
                    sidebarVm.SidebarExpandedWidth = ProfilePanelColumn.Width.Value;
                    sidebarVm.SaveSidebarState();
                }

                if (_closeConfirmed)
                {
                    base.OnClosing(e);
                    return;
                }

                // Always cancel this pass; teardown decides whether to re-invoke Close(). A re-entrant
                // call while the first teardown is still running is cancelled and dropped here.
                e.Cancel = true;
                if (_closing)
                    return;
                _closing = true;

                bool canClose = DataContext is not MainWindowViewModel vm || await vm.RequestCloseAsync();
                if (canClose)
                {
                    _closeConfirmed = true;
                    Close();
                }
                else
                {
                    // The close was vetoed (e.g. user cancelled the active-jobs warning); allow a
                    // later close attempt to run teardown again.
                    _closing = false;
                }
            }
            catch (Exception ex)
            {
                // Last resort: a failure here must never trap the user — and an async void must never
                // let an exception escape and crash the app. Log and force the close.
                Serilog.Log.Error(ex, "Window closing handler failed; forcing close");
                _closeConfirmed = true;
                Close();
            }
        }
    }
}
