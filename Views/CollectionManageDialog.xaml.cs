using GameLauncher.Models;
using GameLauncher.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace GameLauncher.Views
{
    public class CollectionDisplayItem
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public int GameCount { get; set; }
    }

    public sealed partial class CollectionManageDialog : ContentDialog
    {
        private readonly GameService _gameService;
        private ObservableCollection<CollectionDisplayItem> _displayItems = new();
        private CollectionDisplayItem? _renamingItem;
        private CollectionDisplayItem? _deletingItem;

        public CollectionManageDialog(GameService gameService)
        {
            _gameService = gameService;
            this.InitializeComponent();
            CollectionsListView.ItemsSource = _displayItems;
            _ = LoadCollectionsAsync();
        }

        private async Task LoadCollectionsAsync()
        {
            try
            {
                var collections = await _gameService.GetAllCollectionsAsync();
                _displayItems.Clear();
                foreach (var col in collections)
                {
                    var count = await _gameService.GetCollectionGameCountAsync(col.Id);
                    _displayItems.Add(new CollectionDisplayItem
                    {
                        Id = col.Id,
                        Name = col.Name,
                        GameCount = count
                    });
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"加载收藏夹失败: {ex.Message}");
            }
        }

        private async void AddCollectionButton_Click(object sender, RoutedEventArgs e)
        {
            await AddCollectionAsync();
        }

        private async void NewCollectionTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                await AddCollectionAsync();
                e.Handled = true;
            }
        }

        private async Task AddCollectionAsync()
        {
            var name = NewCollectionTextBox.Text?.Trim();
            if (string.IsNullOrEmpty(name))
                return;

            try
            {
                await _gameService.AddCollectionAsync(name);
                NewCollectionTextBox.Text = string.Empty;
                await LoadCollectionsAsync();
            }
            catch (Exception ex)
            {
                var errorDialog = new ContentDialog
                {
                    Title = "错误",
                    Content = $"创建收藏夹失败：{ex.Message}",
                    CloseButtonText = "确定",
                    XamlRoot = XamlRoot,
                    Style = (Style)App.Current.Resources["DefaultContentDialogStyle"]
                };
                await errorDialog.ShowAsync();
            }
        }

        private async void RenameCollectionButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is CollectionDisplayItem item)
            {
                _renamingItem = item;
                RenameTextBox.Text = item.Name;
                RenameTextBox.SelectAll();
                RenamePanel.Visibility = Visibility.Visible;
            }
        }

        private void CancelRenameButton_Click(object sender, RoutedEventArgs e)
        {
            RenamePanel.Visibility = Visibility.Collapsed;
            _renamingItem = null;
            RenameTextBox.Text = string.Empty;
        }

        private async void ConfirmRenameButton_Click(object sender, RoutedEventArgs e)
        {
            await ConfirmRenameAsync();
        }

        private async void RenameTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                await ConfirmRenameAsync();
                e.Handled = true;
            }
        }

        private async Task ConfirmRenameAsync()
        {
            if (_renamingItem == null) return;

            var newName = RenameTextBox.Text?.Trim();
            if (!string.IsNullOrEmpty(newName) && newName != _renamingItem.Name)
            {
                try
                {
                    await _gameService.UpdateCollectionAsync(new GameCollection
                    {
                        Id = _renamingItem.Id,
                        Name = newName
                    });
                    RenamePanel.Visibility = Visibility.Collapsed;
                    _renamingItem = null;
                    RenameTextBox.Text = string.Empty;
                    await LoadCollectionsAsync();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"重命名收藏夹失败: {ex.Message}");
                }
            }
            else
            {
                RenamePanel.Visibility = Visibility.Collapsed;
                _renamingItem = null;
                RenameTextBox.Text = string.Empty;
            }
        }

        private async void DeleteCollectionButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is CollectionDisplayItem item)
            {
                _deletingItem = item;
                DeleteItemName.Text = item.Name;
                DeleteConfirmPanel.Visibility = Visibility.Visible;
            }
        }

        private void CancelDeleteButton_Click(object sender, RoutedEventArgs e)
        {
            DeleteConfirmPanel.Visibility = Visibility.Collapsed;
            _deletingItem = null;
        }

        private async void ConfirmDeleteButton_Click(object sender, RoutedEventArgs e)
        {
            if (_deletingItem == null) return;

            try
            {
                await _gameService.DeleteCollectionAsync(_deletingItem.Id);
                DeleteConfirmPanel.Visibility = Visibility.Collapsed;
                _deletingItem = null;
                await LoadCollectionsAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"删除收藏夹失败: {ex.Message}");
            }
        }
    }
}