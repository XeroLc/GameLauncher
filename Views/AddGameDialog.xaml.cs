using GameLauncher.Data;
using GameLauncher.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace GameLauncher.Views
{
    public sealed partial class AddGameDialog : ContentDialog
    {
        private Game? _existingGame;
        private string _iconPath = string.Empty;
        private ObservableCollection<string> _imagePaths = new ObservableCollection<string>();
        private ObservableCollection<ImageSource> _imageSources = new ObservableCollection<ImageSource>();
        private ObservableCollection<string> _tags = new ObservableCollection<string>();
        private ObservableCollection<string> _allExistingTags = new ObservableCollection<string>();
        private ObservableCollection<CollectionCheckItem> _collectionItems = new();
        private string? _gmdImportPath;
        private Game? _importedGame;

        public string GameName => GameNameTextBox.Text;
        public string ExecutablePath => ExecutablePathTextBox.Text;
        public string IconPath => _iconPath;
        public string Description => DescriptionTextBox.Text;
        public ObservableCollection<string> ImagePaths => _imagePaths;
        public ObservableCollection<string> Tags => _tags;
        public string ImageCountDisplay => _imagePaths.Count > 0 ? $"已选择 {_imagePaths.Count} 张图片" : "未选择图片";
        public bool IsGmdQuickImport => !string.IsNullOrEmpty(_gmdImportPath);
        public Game? ImportedGame => _importedGame;
        public List<int> SelectedCollectionIds
        {
            get
            {
                return _collectionItems.Where(c => c.IsSelected).Select(c => c.Id).ToList();
            }
        }

        public AddGameDialog()
        {
            InitializeComponent();
            PreviewImagesItemsControl.ItemsSource = _imageSources;
            TagsItemsControl.ItemsSource = _tags;
            CollectionsItemsControl.ItemsSource = _collectionItems;
        }

        public AddGameDialog(Game game, Services.ImageService? imageService = null) : this()
        {
            _existingGame = game;
            GameNameTextBox.Text = game.Name;
            ExecutablePathTextBox.Text = game.ExecutablePath;
            _iconPath = game.IconPath ?? string.Empty;
            UpdateIconButtonText();
            DescriptionTextBox.Text = game.Description ?? string.Empty;
            Title = "编辑游戏";

            if (string.IsNullOrEmpty(_iconPath) || !File.Exists(_iconPath))
            {
                if (imageService != null && !string.IsNullOrWhiteSpace(game.GameId))
                {
                    var globalIcon = imageService.GetIconPath(game.GameId);
                    if (File.Exists(globalIcon))
                        _iconPath = globalIcon;
                }
            }

            var pathsToLoad = game.ImagePaths.Where(p => File.Exists(p)).ToList();

            if (pathsToLoad.Count == 0 && imageService != null && !string.IsNullOrWhiteSpace(game.GameId))
            {
                var globalPreviews = imageService.GetAllPreviewImagePaths(game.GameId);
                if (globalPreviews.Count > 0)
                    pathsToLoad = globalPreviews;
            }

            foreach (var imagePath in pathsToLoad)
            {
                _imagePaths.Add(imagePath);
            }
            LoadImages();

            foreach (var tag in game.Tags)
            {
                _tags.Add(tag);
            }

            GmdQuickAddPanel.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
        }

        public void SetExistingTags(List<string> existingTags)
        {
            _allExistingTags.Clear();
            foreach (var tag in existingTags)
            {
                _allExistingTags.Add(tag);
            }
            UpdateTagComboBox();
        }

        public void SetCollections(List<GameCollection> collections, List<int>? selectedIds = null)
        {
            _collectionItems.Clear();
            foreach (var col in collections)
            {
                _collectionItems.Add(new CollectionCheckItem
                {
                    Id = col.Id,
                    Name = col.Name,
                    IsSelected = selectedIds?.Contains(col.Id) ?? false
                });
            }
            if (CollectionsItemsControl != null)
            {
                CollectionsItemsControl.ItemsSource = _collectionItems;
            }
        }

        private void UpdateTagComboBox()
        {
            if (TagComboBox != null)
            {
                var availableTags = _allExistingTags.Where(tag => !_tags.Contains(tag)).OrderBy(tag => tag).ToList();
                TagComboBox.ItemsSource = availableTags;
            }
        }

        private void TagComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (TagComboBox.SelectedItem is string selectedTag)
            {
                TagInputTextBox.Text = selectedTag;
                TagComboBox.SelectedItem = null;
            }
        }

        private void TagInputTextBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                AddTag(TagInputTextBox.Text);
                e.Handled = true;
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
                    XamlRoot = XamlRoot,
                    Style = (Style)App.Current.Resources["DefaultContentDialogStyle"]
                };
                await errorDialog.ShowAsync();
            }
        }

        private async void BrowseGmdButton_Click(object sender, RoutedEventArgs e)
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
                picker.FileTypeFilter.Add(".gmd");

                var file = await picker.PickSingleFileAsync();
                if (file == null) return;

                // 从 .gmd 文件加载游戏数据（图片已通过 ImageService 保存到全局目录）
                var gmdService = new Services.GmdFileService();
                _importedGame = await gmdService.DeserializeGameFromGmdAsync(file.Path);
                _gmdImportPath = file.Path;

                // 直接关闭对话框，由 MainWindow 统一保存到数据库
                this.Hide();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"GMD快速添加失败: {ex.Message}");
                var errorDialog = new ContentDialog
                {
                    Title = "添加失败",
                    Content = $"无法从GMD文件添加游戏：{ex.Message}",
                    CloseButtonText = "确定",
                    XamlRoot = XamlRoot,
                    Style = (Style)App.Current.Resources["DefaultContentDialogStyle"]
                };
                await errorDialog.ShowAsync();
            }
        }

        private async Task LoadFromGmdFileAsync(string gmdFilePath)
        {
            try
            {
                var gmdService = new Services.GmdFileService();
                var game = await gmdService.DeserializeGameFromGmdAsync(gmdFilePath);

                if (!string.IsNullOrEmpty(game.Name))
                    GameNameTextBox.Text = game.Name;

                if (!string.IsNullOrEmpty(game.ExecutablePath))
                    ExecutablePathTextBox.Text = game.ExecutablePath;

                if (!string.IsNullOrEmpty(game.IconPath))
                {
                    _iconPath = game.IconPath;
                    UpdateIconButtonText();
                }

                if (!string.IsNullOrEmpty(game.Description))
                    DescriptionTextBox.Text = game.Description;

                _imagePaths.Clear();
                foreach (var imagePath in game.ImagePaths)
                {
                    _imagePaths.Add(imagePath);
                }
                LoadImages();

                foreach (var tag in game.Tags)
                {
                    if (!_tags.Contains(tag))
                        _tags.Add(tag);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"从.gmd文件加载失败: {ex.Message}");
                var errorDialog = new ContentDialog
                {
                    Title = "提示",
                    Content = $"无法解析.gmd文件：{ex.Message}\n\n您可以手动填写游戏信息。",
                    CloseButtonText = "确定",
                    XamlRoot = XamlRoot,
                    Style = (Style)App.Current.Resources["DefaultContentDialogStyle"]
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
                picker.FileTypeFilter.Add(".webp");
                picker.FileTypeFilter.Add(".gif");

                var file = await picker.PickSingleFileAsync();
                if (file != null)
                {
                    _iconPath = file.Path;
                    UpdateIconButtonText();
                }
            }
            catch (Exception ex)
            {
                var errorDialog = new ContentDialog
                {
                    Title = "错误",
                    Content = $"选择图标时出错：{ex.Message}",
                    CloseButtonText = "确定",
                    XamlRoot = XamlRoot,
                    Style = (Style)App.Current.Resources["DefaultContentDialogStyle"]
                };
                await errorDialog.ShowAsync();
            }
        }

        private void UpdateIconButtonText()
        {
            if (!string.IsNullOrEmpty(_iconPath))
            {
                IconButtonText.Text = System.IO.Path.GetFileName(_iconPath);
            }
            else
            {
                IconButtonText.Text = "选择图标";
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
                picker.FileTypeFilter.Add(".gif");

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
                    XamlRoot = XamlRoot,
                    Style = (Style)App.Current.Resources["DefaultContentDialogStyle"]
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

        private void AddTagButton_Click(object sender, RoutedEventArgs e)
        {
            AddTag(TagInputTextBox.Text);
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
                TagInputTextBox.Text = string.Empty;
                UpdateTagComboBox();
            }
        }

        private void RemoveTagButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.DataContext is string tag)
            {
                _tags.Remove(tag);
                UpdateTagComboBox();
            }
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }

    public class CollectionCheckItem : INotifyPropertyChanged
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected))); }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
}