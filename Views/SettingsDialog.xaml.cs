using System;
using System.Collections.ObjectModel;
using System.Linq;
using GameLauncher.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace GameLauncher.Views
{
    public sealed partial class SettingsDialog : ContentDialog
    {
        private ObservableCollection<string> _scanPaths;
        private bool _isLoaded;

        public SettingsDialog()
        {
            this.InitializeComponent();
            LoadSettings();
        }

        private void LoadSettings()
        {
            var settings = UserSettings.Instance;
            _scanPaths = new ObservableCollection<string>(settings.ScanPaths ?? new System.Collections.Generic.List<string>());
            ScanPathListView.ItemsSource = _scanPaths;
            _isLoaded = true;

            HideUnavailableGamesToggle.IsOn = settings.HideUnavailableGames;
            AutoScanToggle.IsOn = settings.AutoScanEnabled;

            UpdateScanPanelVisibility();
        }

        private void UpdateScanPanelVisibility()
        {
            ScanPathsPanel.Visibility = AutoScanToggle.IsOn
                ? Microsoft.UI.Xaml.Visibility.Visible
                : Microsoft.UI.Xaml.Visibility.Collapsed;
        }

        private void HideUnavailableGamesToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_isLoaded) return;
            var settings = UserSettings.Instance;
            settings.HideUnavailableGames = HideUnavailableGamesToggle.IsOn;
            settings.Save();
        }

        private void AutoScanToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_isLoaded) return;
            var settings = UserSettings.Instance;
            settings.AutoScanEnabled = AutoScanToggle.IsOn;
            UpdateScanPanelVisibility();
            SaveScanPaths();
        }

        private async void AddScanPathButton_Click(object sender, RoutedEventArgs e)
        {
            var folderPicker = new FolderPicker();
            folderPicker.SuggestedStartLocation = PickerLocationId.Desktop;
            folderPicker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(folderPicker,
                WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));

            var folder = await folderPicker.PickSingleFolderAsync();
            if (folder != null && !_scanPaths.Contains(folder.Path))
            {
                _scanPaths.Add(folder.Path);
                SaveScanPaths();
            }
        }

        private void RemoveScanPathButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string path)
            {
                _scanPaths.Remove(path);
                SaveScanPaths();
            }
        }

        private void SaveScanPaths()
        {
            if (_scanPaths == null) return;
            var settings = UserSettings.Instance;
            settings.ScanPaths = _scanPaths.ToList();
            settings.Save();
        }
    }
}