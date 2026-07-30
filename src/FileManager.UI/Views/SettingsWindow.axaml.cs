using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using FileManager.UI.ViewModels;
using FileManager.UI.ViewModels.Settings;
using System;
using System.Linq;

namespace FileManager.UI.Views
{
    public partial class SettingsWindow : Window
    {
        public SettingsWindow()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;
            Closed += OnWindowClosed;
        }

        private SettingsViewModel? _subscribed;

        // Scrolling is a view concern, so the view model only announces "bring this setting into view"
        // and this window decides how (mirrors the RequestClose / folder-picker callback seams).
        private void OnDataContextChanged(object? sender, EventArgs e)
        {
            Unsubscribe();
            _subscribed = DataContext as SettingsViewModel;
            if (_subscribed is not null)
                _subscribed.ScrollToRequested += OnScrollToRequested;
        }

        private void OnWindowClosed(object? sender, EventArgs e) => Unsubscribe();

        private void Unsubscribe()
        {
            if (_subscribed is not null)
                _subscribed.ScrollToRequested -= OnScrollToRequested;
            _subscribed = null;
        }

        /// <summary>Puts the setting the navigation tree selected at the top of the viewport. Deliberately
        /// sets <c>Offset</c> rather than calling <c>BringIntoView</c>: the latter scrolls the minimum
        /// distance, which would park a setting below the fold at the *bottom* edge instead of where the
        /// eye is looking.</summary>
        private void OnScrollToRequested(object? sender, SettingItemViewModel item)
        {
            // The target may have only just become visible (a search was cleared), so its anchor may not
            // be laid out — and an un-laid-out control has no position to scroll to.
            SettingsHost.UpdateLayout();

            if (SettingsScroll.Content is not Control content)
                return;

            // The anchor is the Border.settingCard the per-kind DataTemplate wraps every setting in. A
            // visual-descendant walk rather than a container lookup because the settings live in a nested
            // ItemsControl (category → items), so there is no single container collection to index.
            Border? anchor = SettingsHost.GetVisualDescendants()
                .OfType<Border>()
                .FirstOrDefault(b => b.Classes.Contains("settingCard") && ReferenceEquals(b.DataContext, item));
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
