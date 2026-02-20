using GameLauncher.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace GameLauncher.Views
{
    public sealed partial class AddGameDialog : ContentDialog
    {
        private Game? _existingGame;
        private ObservableCollection<string> _imagePaths = new ObservableCollection<string>();
        private ObservableCollection<ImageSource> _imageSources = new ObservableCollection<ImageSource>();
        private ObservableCollection<string> _tags = new ObservableCollection<string>();
        private ObservableCollection<string> _allExistingTags = new ObservableCollection<string>();

        public string GameName => GameNameTextBox.Text;
        public string ExecutablePath => ExecutablePathTextBox.Text;
        public string IconPath => IconPathTextBox.Text;
        public string Description => DescriptionTextBox.Text;
        public ObservableCollection<string> ImagePaths => _imagePaths;
        public ObservableCollection<string> Tags => _tags;
        public string ImageCountDisplay => _imagePaths.Count > 0 ? $"已选择 {_imagePaths.Count} 张图片" : "未选择图片";

        public AddGameDialog()
        {
            InitializeComponent();
            PreviewImagesItemsControl.ItemsSource = _imageSources;
            TagsItemsControl.ItemsSource = _tags;
            TagAutoSuggestBox.TextChanged += TagAutoSuggestBox_TextChanged;
        }

        public AddGameDialog(Game game) : this()
        {
            _existingGame = game;
            GameNameTextBox.Text = game.Name;
            ExecutablePathTextBox.Text = game.ExecutablePath;
            IconPathTextBox.Text = game.IconPath ?? string.Empty;
            DescriptionTextBox.Text = game.Description ?? string.Empty;
            Title = "编辑游戏";

            // 加载已有的预览图
            foreach (var imagePath in game.ImagePaths)
            {
                _imagePaths.Add(imagePath);
            }
            LoadImages();

            // 加载已有的标签
            foreach (var tag in game.Tags)
            {
                _tags.Add(tag);
            }
        }

        public void SetExistingTags(List<string> existingTags)
        {
            _allExistingTags.Clear();
            foreach (var tag in existingTags)
            {
                _allExistingTags.Add(tag);
            }
            UpdateTagSuggestions();
        }

        private void UpdateTagSuggestions()
        {
            if (TagAutoSuggestBox != null)
            {
                var availableTags = _allExistingTags.Where(tag => !_tags.Contains(tag)).OrderBy(tag => tag).ToList();
                TagAutoSuggestBox.ItemsSource = availableTags;
            }
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
                picker.FileTypeFilter.Add(".bat");
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

        private async void AddPreviewImageButton_Click(object sender, RoutedEventArgs e)
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
                picker.FileTypeFilter.Add(".webp");

                var files = await picker.PickMultipleFilesAsync();
                if (files != null)
                {
                    foreach (var file in files)
                    {
                        if (!_imagePaths.Contains(file.Path))
                        {
                            _imagePaths.Add(file.Path);
                        }
                    }
                    LoadImages();
                }
            }
            catch (Exception ex)
            {
                var errorDialog = new ContentDialog
                {
                    Title = "错误",
                    Content = $"选择预览图时出错：{ex.Message}",
                    CloseButtonText = "确定",
                    XamlRoot = XamlRoot
                };
                await errorDialog.ShowAsync();
            }
        }

        private void DeletePreviewImageButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.DataContext is ImageSource imageSource)
            {
                int index = -1;
                for (int i = 0; i < _imageSources.Count; i++)
                {
                    if (_imageSources[i] == imageSource)
                    {
                        index = i;
                        break;
                    }
                }

                if (index >= 0 && index < _imagePaths.Count)
                {
                    _imagePaths.RemoveAt(index);
                    LoadImages();
                }
            }
        }

        private void LoadImages()
        {
            _imageSources.Clear();
            foreach (var imagePath in _imagePaths)
            {
                if (!string.IsNullOrEmpty(imagePath) && File.Exists(imagePath))
                {
                    try
                    {
                        var bitmapImage = new BitmapImage();
                        bitmapImage.UriSource = new Uri(imagePath);
                        _imageSources.Add(bitmapImage);
                    }
                    catch
                    {
                        // 忽略加载失败的图片
                    }
                }
            }

            // 更新计数显示
            OnPropertyChanged(nameof(ImageCountDisplay));
        }

        private void OnPropertyChanged(string propertyName)
        {
            // 触发属性变更通知以更新绑定
            var eventArgs = new System.ComponentModel.PropertyChangedEventArgs(propertyName);
            PropertyChanged?.Invoke(this, eventArgs);
        }

        private void TagAutoSuggestBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
        {
            if (args.ChosenSuggestion != null)
            {
                AddTag(args.ChosenSuggestion.ToString()!);
            }
            else
            {
                AddTag(args.QueryText);
            }
        }

        private void TagAutoSuggestBox_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
        {
            if (args.SelectedItem != null)
            {
                sender.Text = args.SelectedItem.ToString();
            }
        }

        private void AddTagButton_Click(object sender, RoutedEventArgs e)
        {
            AddTag(TagAutoSuggestBox.Text);
        }

        private void AddTag(string tagText)
        {
            if (!string.IsNullOrWhiteSpace(tagText))
            {
                var trimmedTag = tagText.Trim();
                if (!_tags.Contains(trimmedTag))
                {
                    _tags.Add(trimmedTag);
                }
                TagAutoSuggestBox.Text = string.Empty;
                UpdateTagSuggestions();
            }
        }

        private void RemoveTagButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.DataContext is string tag)
            {
                _tags.Remove(tag);
                UpdateTagSuggestions();
            }
        }

        private void TagAutoSuggestBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
            {
                var inputText = sender.Text?.Trim();
                if (!string.IsNullOrWhiteSpace(inputText))
                {
                    var lowerInput = inputText.ToLowerInvariant();
                    var suggestions = _allExistingTags
                        .Where(tag => !_tags.Contains(tag) && tag.ToLowerInvariant().Contains(lowerInput))
                        .OrderBy(tag => tag)
                        .ToList();
                    sender.ItemsSource = suggestions;
                }
                else
                {
                    UpdateTagSuggestions();
                }
            }
        }

        private void TagAutoSuggestBox_Loaded(object sender, RoutedEventArgs e)
        {
            UpdateTagSuggestions();
        }

        private void TagAutoSuggestBox_GotFocus(object sender, RoutedEventArgs e)
        {
            UpdateTagSuggestions();
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }
}