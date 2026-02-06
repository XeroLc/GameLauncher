using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using GameLauncher.Data;
using GameLauncher.Models;
using GameLauncher.Services;
using GameLauncher.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace GameLauncher
{
    public sealed partial class MainWindow : Window
    {
        private readonly DatabaseContext _dbContext;
        private readonly GameRepository _repository;
        private readonly GameService _gameService;
        private readonly ObservableCollection<Game> _games;
        private bool _initialized = false;

        public ObservableCollection<Game> Games => _games;

        public MainWindow()
        {
            InitializeComponent();

            _dbContext = new DatabaseContext();
            _repository = new GameRepository(_dbContext);
            _gameService = new GameService(_repository);
            _games = new ObservableCollection<Game>();

            Activated += MainWindow_Activated;
        }

        private async void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
        {
            if (!_initialized)
            {
                _initialized = true;
                Activated -= MainWindow_Activated;
                
                await System.Threading.Tasks.Task.Delay(100);
                
                try
                {
                    var initializer = new DatabaseInitializer(_dbContext);
                    await initializer.InitializeAsync();
                    await LoadGamesAsync();
                }
                catch (Exception ex)
                {
                    await ShowErrorDialog("初始化失败", $"数据库初始化失败：{ex.Message}");
                }
            }
        }

        private async System.Threading.Tasks.Task LoadGamesAsync()
        {
            var games = await _gameService.GetAllGamesAsync();
            _games.Clear();
            foreach (var game in games)
            {
                _games.Add(game);
            }

            UpdateEmptyState();
        }

        private void UpdateEmptyState()
        {
            EmptyState.Visibility = _games.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            GamesGridView.Visibility = _games.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        private async void AddGameButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new AddGameDialog
            {
                XamlRoot = Content.XamlRoot
            };

            var result = await dialog.ShowAsync();

            if (result == ContentDialogResult.Primary)
            {
                var newGame = new Game
                {
                    Name = dialog.GameName,
                    ExecutablePath = dialog.ExecutablePath,
                    Description = dialog.Description
                };

                try
                {
                    await _gameService.AddGameAsync(newGame);
                    await LoadGamesAsync();
                }
                catch (Exception ex)
                {
                    await ShowErrorDialog("添加游戏失败", ex.Message);
                }
            }
        }

        private async void LaunchButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.DataContext is Game game)
            {
                var success = await _gameService.LaunchGameAsync(game);
                if (!success)
                {
                    await ShowErrorDialog("启动失败", "无法启动游戏，请检查游戏路径是否正确");
                }
            }
        }

        private async void EditButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.DataContext is Game game)
            {
                var dialog = new AddGameDialog(game)
                {
                    XamlRoot = Content.XamlRoot
                };

                var result = await dialog.ShowAsync();

                if (result == ContentDialogResult.Primary)
                {
                    game.Name = dialog.GameName;
                    game.ExecutablePath = dialog.ExecutablePath;
                    game.Description = dialog.Description;

                    try
                    {
                        await _gameService.UpdateGameAsync(game);
                        await LoadGamesAsync();
                    }
                    catch (Exception ex)
                    {
                        await ShowErrorDialog("更新游戏失败", ex.Message);
                    }
                }
            }
        }

        private async void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.DataContext is Game game)
            {
                var dialog = new ContentDialog
                {
                    Title = "确认删除",
                    Content = $"确定要删除游戏「{game.Name}」吗？",
                    PrimaryButtonText = "删除",
                    CloseButtonText = "取消",
                    XamlRoot = Content.XamlRoot,
                    DefaultButton = ContentDialogButton.Close
                };

                var result = await dialog.ShowAsync();

                if (result == ContentDialogResult.Primary)
                {
                    var success = await _gameService.DeleteGameAsync(game.Id);
                    if (success)
                    {
                        await LoadGamesAsync();
                    }
                    else
                    {
                        await ShowErrorDialog("删除失败", "删除游戏时发生错误");
                    }
                }
            }
        }

        private async System.Threading.Tasks.Task ShowErrorDialog(string title, string message)
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = message,
                CloseButtonText = "确定",
                XamlRoot = Content.XamlRoot
            };

            await dialog.ShowAsync();
        }
    }
}
