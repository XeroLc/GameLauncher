using GameLauncher.Models;
using Windows.UI;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using System;
using System.Collections.ObjectModel;
using System.Linq;

namespace GameLauncher.Views
{
    public sealed partial class GameDetailDialog : ContentDialog
    {
        private Game _game;

        public string GameName => _game?.Name ?? string.Empty;
        public ImageSource GameIconSource => _game?.IconSource;
        public string GameDescription => _game?.Description ?? string.Empty;
        public int LaunchCount => _game?.LaunchCount ?? 0;
        public string PlayTimeDisplay => FormatPlayTime(_game?.TotalPlayTime ?? 0);
        public string CreatedTimeDisplay => _game?.CreatedAt.ToString("yyyy-MM-dd HH:mm") ?? string.Empty;
        public ObservableCollection<ImageSource> ImageSources => _game?.ImageSources ?? new ObservableCollection<ImageSource>();

        public GameDetailDialog(Game game)
        {
            if (game == null)
            {
                throw new ArgumentNullException(nameof(game));
            }

            InitializeComponent();
            _game = game;

            PreviewImagesFlipView.SelectionChanged += (s, e) => UpdateImageIndicators();
            UpdateImageIndicators();
        }

        private void UpdateImageIndicators()
        {
            try
            {
                ImageIndicators.Children.Clear();

                if (_game?.ImageSources == null || _game.ImageSources.Count == 0)
                {
                    NoImagesText.Visibility = Visibility.Visible;
                    PreviewImagesFlipView.Visibility = Visibility.Collapsed;
                    return;
                }

                NoImagesText.Visibility = Visibility.Collapsed;
                PreviewImagesFlipView.Visibility = Visibility.Visible;

                // 创建画刷
                SolidColorBrush tertiaryBrush = new SolidColorBrush(Microsoft.UI.Colors.Gray);
                SolidColorBrush accentBrush;

                try
                {
                    // 方法一：直接获取系统的画刷资源（推荐）
                    accentBrush = (SolidColorBrush)Application.Current.Resources["SystemAccentColorBrush"];
                }
                catch (Exception ex)
                {
                    // 失败时，手动创建画刷
                    System.Diagnostics.Debug.WriteLine($"获取系统画刷失败: {ex.Message}");
                    accentBrush = new SolidColorBrush(Microsoft.UI.Colors.LightBlue);
                }

                for (int i = 0; i < _game.ImageSources.Count; i++)
                {
                    var ellipse = new Ellipse
                    {
                        Width = 8,
                        Height = 8,
                        Fill = tertiaryBrush
                    };

                    if (i == PreviewImagesFlipView.SelectedIndex)
                    {
                        ellipse.Fill = accentBrush;
                        ellipse.Width = 20;
                    }

                    ImageIndicators.Children.Add(ellipse);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"更新图片指示器时出错: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"堆栈跟踪: {ex.StackTrace}");
            }
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

        private void LaunchButton_Click(object sender, RoutedEventArgs e)
        {
            Hide();
        }
    }
}