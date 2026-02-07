using GameLauncher.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.IO;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace GameLauncher.Views
{
    public sealed partial class AddGameDialog : ContentDialog
    {
        private Game? _existingGame;

        public string GameName => GameNameTextBox.Text;
        public string ExecutablePath => ExecutablePathTextBox.Text;
        public string IconPath => IconPathTextBox.Text;
        public string Description => DescriptionTextBox.Text;

        public AddGameDialog()
        {
            InitializeComponent();
        }

        public AddGameDialog(Game game) : this()
        {
            _existingGame = game;
            GameNameTextBox.Text = game.Name;
            ExecutablePathTextBox.Text = game.ExecutablePath;
            IconPathTextBox.Text = game.IconPath ?? string.Empty;
            DescriptionTextBox.Text = game.Description ?? string.Empty;
            Title = "编辑游戏";
        }

        private async void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (App.MainWindow == null) return;

                var hwnd = WindowNative.GetWindowHandle(App.MainWindow);
                var picker = new FileOpenPicker
                {
                    ViewMode = PickerViewMode.List,
                    SuggestedStartLocation = PickerLocationId.ComputerFolder
                };

                InitializeWithWindow.Initialize(picker, hwnd);

                picker.FileTypeFilter.Add(".exe");
                picker.FileTypeFilter.Add(".lnk");

                var file = await picker.PickSingleFileAsync();
                if (file != null)
                {
                    ExecutablePathTextBox.Text = file.Path;

                    if (string.IsNullOrWhiteSpace(GameNameTextBox.Text))
                    {
                        var fileName = file.Name;
                        GameNameTextBox.Text = Path.GetFileNameWithoutExtension(fileName);
                    }
                }
            }
            catch (Exception ex)
            {
                var errorDialog = new ContentDialog
                {
                    Title = "错误",
                    Content = $"选择文件时出错：{ex.Message}",
                    CloseButtonText = "确定",
                    XamlRoot = XamlRoot
                };
                await errorDialog.ShowAsync();
            }
        }

        private async void BrowseIconButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (App.MainWindow == null) return;

                var hwnd = WindowNative.GetWindowHandle(App.MainWindow);
                var picker = new FileOpenPicker
                {
                    ViewMode = PickerViewMode.Thumbnail,
                    SuggestedStartLocation = PickerLocationId.PicturesLibrary
                };

                InitializeWithWindow.Initialize(picker, hwnd);

                picker.FileTypeFilter.Add(".png");
                picker.FileTypeFilter.Add(".jpg");
                picker.FileTypeFilter.Add(".jpeg");
                picker.FileTypeFilter.Add(".bmp");
                picker.FileTypeFilter.Add(".ico");

                var file = await picker.PickSingleFileAsync();
                if (file != null)
                {
                    IconPathTextBox.Text = file.Path;
                }
            }
            catch (Exception ex)
            {
                var errorDialog = new ContentDialog
                {
                    Title = "错误",
                    Content = $"选择图标时出错：{ex.Message}",
                    CloseButtonText = "确定",
                    XamlRoot = XamlRoot
                };
                await errorDialog.ShowAsync();
            }
        }
    }
}