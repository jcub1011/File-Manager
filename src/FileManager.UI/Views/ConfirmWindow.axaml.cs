using Avalonia.Controls;
using Avalonia.Interactivity;

namespace FileManager.UI.Views
{
    /// <summary>Minimal modal Yes/No confirmation returning a bool via ShowDialog&lt;bool&gt;. The repo
    /// has no reusable message box, so this is the tiny shared one for close-time confirmations.</summary>
    public partial class ConfirmWindow : Window
    {
        public ConfirmWindow()
        {
            InitializeComponent();
        }

        public ConfirmWindow(string message) : this()
        {
            MessageText.Text = message;
        }

        /// <summary>Yes/No confirmation with custom button labels. <paramref name="confirmIsDanger"/>
        /// keeps the destructive red styling on the confirm button (true for close-time warnings);
        /// pass false for a neutral choice such as relocating profiles.</summary>
        public ConfirmWindow(string message, string yesText, string noText, bool confirmIsDanger = true) : this()
        {
            MessageText.Text = message;
            YesButton.Content = yesText;
            NoButton.Content = noText;
            if (!confirmIsDanger)
                YesButton.Classes.Remove("danger");
        }

        private void OnYesClick(object? sender, RoutedEventArgs e) => Close(true);

        private void OnNoClick(object? sender, RoutedEventArgs e) => Close(false);
    }
}
