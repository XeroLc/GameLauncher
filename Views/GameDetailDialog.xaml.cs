using GameLauncher.Models;
using GameLauncher.Services;
using GameLauncher.Data;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.ObjectModel;

namespace GameLauncher.Views
{
    public sealed partial class GameDetailDialog : ContentDialog
    {
        private Game _game;
        private readonly GameService _gameService;

        // 绑定属性
        public string GameName => _game?.Name ?? string.Empty;
        public ImageSource GameIconSource => _game?.IconSource;
        public string GameDescription => _game?.Description ?? string.Empty;
        public int LaunchCount => _game?.LaunchCount ?? 0;
        public string PlayTimeDisplay => FormatPlayTime(_game?.TotalPlayTime ?? 0);
        public string CreatedTimeDisplay => _game?.CreatedAt.ToString("yyyy-MM-dd HH:mm") ?? string.Empty;

        // 预览图集合
        public ObservableCollection<ImageSource> ImageSources => _game?.ImageSources ?? new ObservableCollection<ImageSource>();

        public GameDetailDialog(Game game)
        {
            if (game == null)
            {
                throw new ArgumentNullException(nameof(game));
            }

            _game = game;
            var dbContext = new DatabaseContext();
            var repository = new GameRepository(dbContext);
            _gameService = new GameService(repository);

            // 注意：InitializeComponent 必须在变量赋值后调用，
            // 确保 x:Bind 能够正确找到数据
            this.InitializeComponent();
        }

        private string FormatPlayTime(long totalSeconds)
        {
            if (totalSeconds < 60)
            {
                return $"{totalSeconds}秒";
            }
            else if (totalSeconds < 3600)
            {
                var minutes = totalSeconds / 60;
                return $"{minutes}分钟";
            }
            else
            {
                var hours = totalSeconds / 3600;
                if (hours >= 24)
                {
                    var days = hours / 24;
                    return $"{days}天";
                }
                else
                {
                    return $"{hours}小时";
                }
            }
        }

        private async void LaunchButton_Click(object sender, RoutedEventArgs e)
        {
            if (_game == null)
            {
                return;
            }

            var success = await _gameService.LaunchGameAsync(_game);
            if (success)
            {
                Hide();
            }
            else
            {
                var errorDialog = new ContentDialog
                {
                    Title = "启动失败",
                    Content = "无法启动游戏，请检查游戏路径是否正确",
                    CloseButtonText = "确定",
                    XamlRoot = XamlRoot
                };
                await errorDialog.ShowAsync();
            }
        }
    }
}