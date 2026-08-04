using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using FileManager.UI.ViewModels;
using FileManager.UI.ViewModels.Settings;
using System;
using System.Linq;
using System.Windows.Input;

namespace FileManager.UI.Views
{
    public partial class SettingsWindow : Window
    {
        // Parsed from the same strings the footer buttons show as tooltips, so the display and the
        // handling cannot drift apart (same recipe as DryRunView's row shortcuts).
        private static readonly KeyGesture UndoGesture = KeyGesture.Parse("Ctrl+Z");
        private static readonly KeyGesture RedoGesture = KeyGesture.Parse("Ctrl+Y");
        private static readonly KeyGesture RedoAltGesture = KeyGesture.Parse("Ctrl+Shift+Z");

        public SettingsWindow()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;
            Closed += OnWindowClosed;

            // Tunnel (preview) routing, so the window sees Ctrl+Z before the focused TextBox does. That
            // is a deliberate trade: undo always means "undo the last settings change" no matter where
            // focus is, at the cost of a TextBox's own per-character undo. Coalescing makes a typing run
            // one settings step, so reverting a path still takes one press.
            AddHandler(InputElement.KeyDownEvent, OnUndoRedoKeyDown, RoutingStrategies.Tunnel);

            // A field losing focus ends its coalescing run, so coming back to it later starts a fresh
            // undo step instead of extending an edit the user has moved on from.
            AddHandler(InputElement.LostFocusEvent, OnEditorLostFocus, RoutingStrategies.Tunnel);
        }

        private void OnUndoRedoKeyDown(object? sender, KeyEventArgs e)
        {
            if (DataContext is not SettingsViewModel vm)
                return;

            ICommand? command =
                UndoGesture.Matches(e) ? vm.History.UndoCommand
                : RedoGesture.Matches(e) || RedoAltGesture.Matches(e) ? vm.History.RedoCommand
                : null;

            // Only claim the key when there is actually something to undo — otherwise Ctrl+Z on an
            // untouched window should behave as it always did rather than being swallowed here.
            if (command is null || !command.CanExecute(null))
                return;
            command.Execute(null);
            e.Handled = true;
        }

        private void OnEditorLostFocus(object? sender, RoutedEventArgs e)
        {
            if (DataContext is SettingsViewModel vm)
                vm.History.BreakMerge();
        }

        private SettingsViewModel? _subscribed;

        // Scrolling is a view concern, so the view model only announces "bring this setting into view"
        // and this window decides how (mirrors the RequestClose / folder-picker callback seams).
        private void OnDataContextChanged(object? sender, EventArgs e)
        {
            Unsubscribe();
            _subscribed = DataContext as SettingsViewModel;
            if (_subscribed is not null)
            {
                _subscribed.ScrollToRequested += OnScrollToRequested;
                _subscribed.ScrollToCategoryRequested += OnScrollToCategoryRequested;
            }
        }

        private void OnWindowClosed(object? sender, EventArgs e) => Unsubscribe();

        private void Unsubscribe()
        {
            if (_subscribed is not null)
            {
                _subscribed.ScrollToRequested -= OnScrollToRequested;
                _subscribed.ScrollToCategoryRequested -= OnScrollToCategoryRequested;
            }
            _subscribed = null;
        }

        // The anchor is the Border.settingCard the per-kind DataTemplate wraps every setting in.
        private void OnScrollToRequested(object? sender, SettingItemViewModel item) =>
            ScrollAnchorIntoView("settingCard", item);

        // The anchor is the Border.sectionHeader the document wraps every category header in.
        private void OnScrollToCategoryRequested(object? sender, SettingsCategoryViewModel category) =>
            ScrollAnchorIntoView("sectionHeader", category);

        /// <summary>Puts the <see cref="Border"/> tagged with <paramref name="anchorClass"/> whose
        /// <c>DataContext</c> is <paramref name="target"/> at the top of the viewport. Deliberately sets
        /// <c>Offset</c> rather than calling <c>BringIntoView</c>: the latter scrolls the minimum
        /// distance, which would park a setting below the fold at the *bottom* edge instead of where the
        /// eye is looking.</summary>
        private void ScrollAnchorIntoView(string anchorClass, object target)
        {
            // The target may have only just become visible (a search was cleared), so its anchor may not
            // be laid out — and an un-laid-out control has no position to scroll to.
            SettingsHost.UpdateLayout();

            if (SettingsScroll.Content is not Control content)
                return;

            // A visual-descendant walk rather than a container lookup because the settings/categories live
            // in a nested ItemsControl (category → items), so there is no single container collection to
            // index.
            Border? anchor = SettingsHost.GetVisualDescendants()
                .OfType<Border>()
                .FirstOrDefault(b => b.Classes.Contains(anchorClass) && ReferenceEquals(b.DataContext, target));
            if (anchor is null)
                return;

            Point? origin = anchor.TranslatePoint(default, content);
            if (origin is null)
                return;

            double max = Math.Max(0, SettingsScroll.Extent.Height - SettingsScroll.Viewport.Height);
            SettingsScroll.Offset = SettingsScroll.Offset.WithY(Math.Clamp(origin.Value.Y - 8, 0, max));
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            // Ctrl+F and Ctrl+E both jump to the search box (the two habits a VS user arrives with).
            if (e.KeyModifiers == KeyModifiers.Control && e.Key is Key.F or Key.E)
            {
                SearchBox.Focus();
                SearchBox.SelectAll();
                e.Handled = true;
                return;
            }

            // Escape backs out one step at a time: drop the filter first, close only once it is gone.
            if (e.Key == Key.Escape)
            {
                if (DataContext is SettingsViewModel vm && !string.IsNullOrEmpty(vm.SearchText))
                    vm.SearchText = null;
                else
                    Close();
                e.Handled = true;
                return;
            }

            base.OnKeyDown(e);
        }

        private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
    }
}
