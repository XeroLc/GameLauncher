using GameLauncher.Models;
using Microsoft.UI.Xaml.Controls;
using System;

namespace GameLauncher.Views
{
    public sealed partial class SettingsDialog : ContentDialog
    {
        private readonly Action? _onSettingsChanged;

        public SettingsDialog(Action? onSettingsChanged = null)
        {
            _onSettingsChanged = onSettingsChanged;
            this.InitializeComponent();

            var settings = UserSettings.Instance;
            HideUnavailableGamesToggle.IsOn = settings.HideUnavailableGames;
        }

        private void HideUnavailableGamesToggle_Toggled(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            var settings = UserSettings.Instance;
            settings.HideUnavailableGames = HideUnavailableGamesToggle.IsOn;
            settings.Save();

            _onSettingsChanged?.Invoke();
        }
    }
}
