using GameLauncher.Models;
using GameLauncher.Services;
using GameLauncher.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Media3D;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace GameLauncher.Views
{
    public sealed partial class GameDetailDialogView : ContentDialog
    {
        private Game _game;
        private readonly GameService _gameService;
        private readonly GameImageLoader _gameImageLoader;
        private readonly ImageService _imageService;
        private readonly GameArchiveService _archiveService;
        private readonly List<string> _allExistingTags;
        private DateTime? _gameStartTime;
        private bool _isBusy;

        public event Action<Game, DateTime>? GameLaunched;
        public event Action<Game>? GameStopped;
        public event Action? DataChanged;
        public event Action<string, string>? ShowToastRequested;
        /// <summary>带类型的 Toast（Info/Success/Warning/Error）</summary>
        public event Action<string, string, ToastType>? ShowTypedToastRequested;
        /// <summary>传输任务状态变化（true=开始, false=结束），MainWindow 据此驱动卡片进度条</summary>
        public event Action<Game, bool>? TransferStateChanged;
        /// <summary>传输进度更新，MainWindow 据此同步卡片进度条</summary>
        public event Action<Game, ArchiveProgress>? TransferProgressChanged;

        public bool DeleteRequested { get; private set; } = false;

        // 绑定属性
        public string GameName => _game?.Name ?? string.Empty;
        public ImageSource GameIconSource => _game?.IconSource;
        public string GameDescription => _game?.Description ?? string.Empty;
        public int LaunchCount => _game?.LaunchCount ?? 0;
        public string PlayTimeDisplay => FormatPlayTime(_game?.TotalPlayTime ?? 0);
        public string CreatedTimeDisplay => _game?.CreatedAt.ToString("yyyy-MM-dd HH:mm") ?? string.Empty;

        // 预览图集合
        public ObservableCollection<ImageSource> ImageSources => _game?.ImageSources ?? new ObservableCollection<ImageSource>();

        // 标签集合
        public ObservableCollection<string> Tags => _game?.Tags ?? new ObservableCollection<string>();

        // 大图预览
        private ImageSource _largeImageSource;
        private Image _largePreviewImage;
        private Border _largePreviewBorder;

        public ImageSource LargeImageSource
        {
            get => _largeImageSource;
            private set
            {
                _largeImageSource = value;
            }
        }

        public GameDetailDialogView(Game game, GameService gameService, GameImageLoader gameImageLoader, List<string>? allExistingTags = null, DateTime? gameStartTime = null)
        {
            if (game == null)
            {
                throw new ArgumentNullException(nameof(game));
            }

            _game = game;
            _gameService = gameService ?? throw new ArgumentNullException(nameof(gameService));
            _gameImageLoader = gameImageLoader ?? throw new ArgumentNullException(nameof(gameImageLoader));
            _imageService = App.Services.GetRequiredService<ImageService>();
            _archiveService = App.Services.GetRequiredService<GameArchiveService>();
            _allExistingTags = allExistingTags ?? new List<string>();
            _gameStartTime = gameStartTime;

            // 注意：InitializeComponent 必须在变量赋值后调用，
            // 确保 x:Bind 能够正确找到数据
            this.InitializeComponent();

            _gameImageLoader.LoadImages(_game);

            // 获取大图预览控件的引用
            _largePreviewImage = FindName("LargePreviewImage") as Image;
            _largePreviewBorder = FindName("LargePreviewBorder") as Border;

            UpdateButtonStates();
        }

        private void Image_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Grid grid)
            {
                // 显示悬停覆盖层
                var overlay = grid.FindName("HoverOverlay") as Border;
                if (overlay != null)
                {
                    overlay.Visibility = Visibility.Visible;
                }
            }
        }

        private void Image_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Grid grid)
            {
                // 隐藏悬停覆盖层
                var overlay = grid.FindName("HoverOverlay") as Border;
                if (overlay != null)
                {
                    overlay.Visibility = Visibility.Collapsed;
                }
            }
        }

        private void Image_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (sender is Grid grid && grid.Children.Count > 0)
            {
                if (grid.Children[0] is Border border && border.Child is Image image)
                {
                    LargeImageSource = image.Source;
                    ShowLargePreview();
                }
            }
        }

        private void ImageOverlay_Tapped(object sender, TappedRoutedEventArgs e)
        {
            HideLargePreview();
        }

        private DoubleAnimation CreateDoubleAnimation(
            DependencyObject target,
            string propertyPath,
            double from,
            double to,
            double durationMs,
            EasingFunctionBase easingFunction)
        {
            var animation = new DoubleAnimation
            {
                From = from,
                To = to,
                Duration = new Duration(TimeSpan.FromMilliseconds(durationMs)),
                EasingFunction = easingFunction
            };
            Storyboard.SetTarget(animation, target);
            Storyboard.SetTargetProperty(animation, propertyPath);
            return animation;
        }

        private void ShowLargePreview()
        {
            if (ImageOverlay == null || LargePreviewBorder == null || PreviewScaleTransform == null || LargePreviewImage == null)
                return;

            LargePreviewImage.Source = _largeImageSource;

            ImageOverlay.Visibility = Visibility.Visible;
            PreviewScaleTransform.ScaleX = 0.6;
            PreviewScaleTransform.ScaleY = 0.6;
            LargePreviewBorder.Opacity = 0;

            var storyboard = new Storyboard();
            storyboard.Children.Add(CreateDoubleAnimation(ImageOverlay, "Opacity", 0, 1, 250, new QuadraticEase()));
            storyboard.Children.Add(CreateDoubleAnimation(PreviewScaleTransform, "ScaleX", 0.6, 1.0, 300, new CubicEase { EasingMode = EasingMode.EaseOut }));
            storyboard.Children.Add(CreateDoubleAnimation(PreviewScaleTransform, "ScaleY", 0.6, 1.0, 300, new CubicEase { EasingMode = EasingMode.EaseOut }));
            storyboard.Children.Add(CreateDoubleAnimation(LargePreviewBorder, "Opacity", 0, 1, 200, new QuadraticEase()));
            storyboard.Begin();
        }

        private void HideLargePreview()
        {
            if (ImageOverlay == null || LargePreviewBorder == null || PreviewScaleTransform == null)
                return;

            var storyboard = new Storyboard();
            storyboard.Children.Add(CreateDoubleAnimation(ImageOverlay, "Opacity", 1, 0, 200, new QuadraticEase()));
            storyboard.Children.Add(CreateDoubleAnimation(PreviewScaleTransform, "ScaleX", 1.0, 0.85, 150, new CubicEase { EasingMode = EasingMode.EaseIn }));
            storyboard.Children.Add(CreateDoubleAnimation(PreviewScaleTransform, "ScaleY", 1.0, 0.85, 150, new CubicEase { EasingMode = EasingMode.EaseIn }));
            storyboard.Children.Add(CreateDoubleAnimation(LargePreviewBorder, "Opacity", 1, 0, 150, new QuadraticEase()));

            storyboard.Completed += (s, e) =>
            {
                ImageOverlay.Visibility = Visibility.Collapsed;
                LargeImageSource = null;
                if (LargePreviewImage != null)
                {
                    LargePreviewImage.Source = null;
                }
            };

            storyboard.Begin();
        }

        private string FormatPlayTime(long totalSeconds)
        {
            return Game.FormatPlayTime(totalSeconds);
        }

        private async void EditButton_Click(object sender, RoutedEventArgs e)
        {
            Hide();

            var editDialog = new AddGameDialog(_game, _imageService);
            editDialog.XamlRoot = XamlRoot;
            editDialog.SetExistingTags(_allExistingTags);

            List<GameCollection> allCollections = new List<GameCollection>();

            try
            {
                allCollections = await _gameService.GetAllCollectionsAsync();
                var gameCollections = await _gameService.GetCollectionsForGameAsync(_game.Id);
                editDialog.SetCollections(allCollections, gameCollections.Select(c => c.Id).ToList());
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"加载游戏收藏夹数据失败: {ex.Message}");
            }

            var result = await editDialog.ShowAsync();

            if (result == ContentDialogResult.Primary)
            {
                await _gameService.UpdateGameFromDialogAsync(_game, editDialog, _imageService, () =>
                {
                    _gameImageLoader.LoadIcon(_game);
                    _gameImageLoader.LoadImages(_game);
                });
                DataChanged?.Invoke();
            }
        }

        private void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            DeleteRequested = true;
            Hide();
        }

        private void OpenPathButton_Click(object sender, RoutedEventArgs e)
        {
            if (_game == null || string.IsNullOrEmpty(_game.ExecutablePath))
                return;

            try
            {
                var directory = System.IO.Path.GetDirectoryName(_game.ExecutablePath);
                if (!string.IsNullOrEmpty(directory) && System.IO.Directory.Exists(directory))
                {
                    System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{_game.ExecutablePath}\"");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"打开游戏路径失败: {ex.Message}");
            }
        }

        private async void LaunchButton_Click(object sender, RoutedEventArgs e)
        {
            if (_game == null || _isBusy)
            {
                return;
            }

            if (!_game.IsInstalled && _game.HasCloudBackup)
            {
                await DownloadAndRestoreAsync();
                return;
            }

            var success = await _gameService.LaunchGameAsync(_game);
            if (success)
            {
                _gameStartTime = DateTime.UtcNow;
                GameLaunched?.Invoke(_game, _gameStartTime.Value);
                ShowStopButton();
            }
            else
            {
                ShowTypedToastRequested?.Invoke("启动失败", "无法启动游戏，请检查游戏路径是否正确", ToastType.Error);
            }
        }

        private void UpdateButtonStates()
        {
            var launchScale = FindName("LaunchScaleTransform") as ScaleTransform;
            var stopScale = FindName("StopScaleTransform") as ScaleTransform;

            // 本地无文件且已有云备份时，没有可归档的内容，隐藏归档按钮
            if (!_game.IsInstalled && _game.HasCloudBackup)
            {
                ArchiveButton.Visibility = Visibility.Collapsed;
                ArchiveProgressPanel.Visibility = Visibility.Collapsed;
            }
            else
            {
                ArchiveButton.Visibility = Visibility.Visible;
                ArchiveButton.IsEnabled = !_game.IsRunning && !_isBusy;
            }

            if (_game.IsRunning)
            {
                LaunchButton.Visibility = Visibility.Collapsed;
                StopButton.Visibility = Visibility.Visible;
                if (stopScale != null)
                {
                    stopScale.ScaleX = 1.0;
                    stopScale.ScaleY = 1.0;
                }
            }
            else
            {
                LaunchButton.Visibility = Visibility.Visible;
                StopButton.Visibility = Visibility.Collapsed;
                if (launchScale != null)
                {
                    launchScale.ScaleX = 1.0;
                    launchScale.ScaleY = 1.0;
                }
                UpdateLaunchButtonLabel();
            }
        }

        private void UpdateLaunchButtonLabel()
        {
            if (LaunchButtonText == null || LaunchButtonIcon == null)
                return;

            if (!_game.IsInstalled && _game.HasCloudBackup)
            {
                LaunchButtonText.Text = "下载游戏";
                LaunchButtonIcon.Glyph = "";
            }
            else
            {
                LaunchButtonText.Text = "启动游戏";
                LaunchButtonIcon.Glyph = "";
            }
        }

        private async void ArchiveButton_Click(object sender, RoutedEventArgs e)
        {
            if (_game == null || _isBusy)
                return;

            var settings = Models.UserSettings.Instance;
            if (string.IsNullOrWhiteSpace(settings.Pan123ClientId) ||
                string.IsNullOrWhiteSpace(SecretProtector.Decrypt(settings.Pan123ClientSecret)))
            {
                ShowTypedToastRequested?.Invoke("未配置 123 云盘", "请先在设置中填写 123 云盘凭据并完成授权", ToastType.Warning);
                return;
            }

            if (_game.IsRunning)
            {
                ShowTypedToastRequested?.Invoke("无法归档", "游戏正在运行，请先停止游戏再归档", ToastType.Warning);
                return;
            }

            _isBusy = true;
            UpdateButtonStates();
            ArchiveButton.Visibility = Visibility.Collapsed;
            ArchiveProgressPanel.Visibility = Visibility.Visible;
            if (ArchiveFillScale != null) ArchiveFillScale.ScaleX = 0;
            ArchiveProgressText.Text = "正在打包...";
            TransferStateChanged?.Invoke(_game, true);

            var progress = new Progress<ArchiveProgress>(p =>
            {
                UpdateArchiveProgress(p);
                TransferProgressChanged?.Invoke(_game, p);
            });
            try
            {
                await _archiveService.ArchiveGameAsync(_game, progress);
                DataChanged?.Invoke();
                ShowTypedToastRequested?.Invoke("归档成功", $"{_game.Name} 已上传到 123 云盘", ToastType.Success);

                // 必须先关闭本详情对话框，才能再弹确认框（WinUI 同一时间只允许一个 ContentDialog）
                Hide();
                await ConfirmDeleteLocalFilesAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ArchiveDialog] 归档失败: {ex.GetType().Name}: {ex.Message}");
                if (ex.InnerException != null)
                    System.Diagnostics.Debug.WriteLine($"[ArchiveDialog] 内部: {ex.InnerException.Message}");
                ShowTypedToastRequested?.Invoke("归档失败", ex.Message, ToastType.Error);
            }
            finally
            {
                _isBusy = false;
                TransferStateChanged?.Invoke(_game, false);
                ArchiveButton.Visibility = Visibility.Visible;
                ArchiveProgressPanel.Visibility = Visibility.Collapsed;
                UpdateButtonStates();
            }
        }

        private void UpdateArchiveProgress(ArchiveProgress p)
        {
            if (ArchiveFillScale != null)
                ArchiveFillScale.ScaleX = Math.Clamp(p.OverallPercent / 100.0, 0, 1);
            if (ArchiveProgressText != null)
                ArchiveProgressText.Text = p.CurrentFile;
        }

        private async Task DownloadAndRestoreAsync()
        {
            _isBusy = true;
            UpdateButtonStates();
            LaunchButton.Visibility = Visibility.Collapsed;
            TransferProgressPanel2.Visibility = Visibility.Visible;
            if (TransferFillScale2 != null) TransferFillScale2.ScaleX = 0;
            TransferStateChanged?.Invoke(_game, true);

            var progress = new Progress<ArchiveProgress>(p =>
            {
                if (TransferFillScale2 != null)
                    TransferFillScale2.ScaleX = Math.Clamp(p.OverallPercent / 100.0, 0, 1);
                if (TransferProgressText2 != null)
                    TransferProgressText2.Text = p.CurrentFile;
                TransferProgressChanged?.Invoke(_game, p);
            });
            try
            {
                await _archiveService.DownloadGameAsync(_game, progress);
                ShowTypedToastRequested?.Invoke("恢复完成", $"{_game.Name} 已恢复到本地", ToastType.Success);
                DataChanged?.Invoke();
            }
            catch (Exception ex)
            {
                ShowTypedToastRequested?.Invoke("下载失败", ex.Message, ToastType.Error);
            }
            finally
            {
                _isBusy = false;
                TransferStateChanged?.Invoke(_game, false);
                TransferProgressPanel2.Visibility = Visibility.Collapsed;
                UpdateButtonStates();
            }
        }

        private async Task ConfirmDeleteLocalFilesAsync()
        {
            if (_game == null) return;

            var dialog = new ContentDialog
            {
                Title = "归档完成",
                Content = $"游戏已上传到 123 云盘。\n是否删除本地游戏文件以释放空间？（云端随时可下载恢复）",
                PrimaryButtonText = "删除本地文件",
                CloseButtonText = "保留",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = App.MainWindow?.Content?.XamlRoot
            };
            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                try
                {
                    var dir = System.IO.Path.GetDirectoryName(_game.ExecutablePath);
                    if (!string.IsNullOrEmpty(dir) && System.IO.Directory.Exists(dir))
                    {
                        System.IO.Directory.Delete(dir, true);
                        DataChanged?.Invoke();
                        ShowTypedToastRequested?.Invoke("已删除本地文件", $"「{_game.Name}」的本地文件已删除，可随时从云盘下载恢复", ToastType.Success);
                    }
                }
                catch (Exception ex)
                {
                    ShowTypedToastRequested?.Invoke("删除失败", ex.Message, ToastType.Error);
                }
            }
        }

        private async void StopButton_Click(object sender, RoutedEventArgs e)
        {
            if (_game == null)
            {
                return;
            }

            var success = await _gameService.StopGameAsync(_game, _gameStartTime);
            if (success)
            {
                GameStopped?.Invoke(_game);
                ShowLaunchButton();
            }
        }

        private void ShowStopButton()
        {
            if (LaunchButton == null || StopButton == null || LaunchScaleTransform == null || StopScaleTransform == null)
                return;

            LaunchButton.IsEnabled = false;

            var scaleXOut = new DoubleAnimation
            {
                From = 1.0,
                To = 0.5,
                Duration = new Duration(TimeSpan.FromMilliseconds(150)),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            };
            Storyboard.SetTarget(scaleXOut, LaunchScaleTransform);
            Storyboard.SetTargetProperty(scaleXOut, "ScaleX");

            var scaleYOut = new DoubleAnimation
            {
                From = 1.0,
                To = 0.5,
                Duration = new Duration(TimeSpan.FromMilliseconds(150)),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            };
            Storyboard.SetTarget(scaleYOut, LaunchScaleTransform);
            Storyboard.SetTargetProperty(scaleYOut, "ScaleY");

            var opacityOut = new DoubleAnimation
            {
                From = 1.0,
                To = 0,
                Duration = new Duration(TimeSpan.FromMilliseconds(150)),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            };
            Storyboard.SetTarget(opacityOut, LaunchButton);
            Storyboard.SetTargetProperty(opacityOut, "Opacity");

            var storyboardOut = new Storyboard();
            storyboardOut.Children.Add(scaleXOut);
            storyboardOut.Children.Add(scaleYOut);
            storyboardOut.Children.Add(opacityOut);
            storyboardOut.Begin();

            storyboardOut.Completed += (s, e) =>
            {
                LaunchButton.Visibility = Visibility.Collapsed;
                StopButton.Visibility = Visibility.Visible;

                StopScaleTransform.ScaleX = 0.5;
                StopScaleTransform.ScaleY = 0.5;

                var scaleXIn = new DoubleAnimation
                {
                    From = 0.5,
                    To = 1.0,
                    Duration = new Duration(TimeSpan.FromMilliseconds(150)),
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };
                Storyboard.SetTarget(scaleXIn, StopScaleTransform);
                Storyboard.SetTargetProperty(scaleXIn, "ScaleX");

                var scaleYIn = new DoubleAnimation
                {
                    From = 0.5,
                    To = 1.0,
                    Duration = new Duration(TimeSpan.FromMilliseconds(150)),
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };
                Storyboard.SetTarget(scaleYIn, StopScaleTransform);
                Storyboard.SetTargetProperty(scaleYIn, "ScaleY");

                var opacityIn = new DoubleAnimation
                {
                    From = 0,
                    To = 1,
                    Duration = new Duration(TimeSpan.FromMilliseconds(150)),
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };
                Storyboard.SetTarget(opacityIn, StopButton);
                Storyboard.SetTargetProperty(opacityIn, "Opacity");

                var storyboardIn = new Storyboard();
                storyboardIn.Children.Add(scaleXIn);
                storyboardIn.Children.Add(scaleYIn);
                storyboardIn.Children.Add(opacityIn);
                storyboardIn.Begin();

                storyboardIn.Completed += (s2, e2) =>
                {
                    StopButton.IsEnabled = true;
                };
            };
        }

        private void ShowLaunchButton()        {
            if (LaunchButton == null || StopButton == null || LaunchScaleTransform == null || StopScaleTransform == null)
                return;

            StopButton.IsEnabled = false;

            var scaleXOut = new DoubleAnimation
            {
                From = 1.0,
                To = 0.5,
                Duration = new Duration(TimeSpan.FromMilliseconds(150)),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            };
            Storyboard.SetTarget(scaleXOut, StopScaleTransform);
            Storyboard.SetTargetProperty(scaleXOut, "ScaleX");

            var scaleYOut = new DoubleAnimation
            {
                From = 1.0,
                To = 0.5,
                Duration = new Duration(TimeSpan.FromMilliseconds(150)),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            };
            Storyboard.SetTarget(scaleYOut, StopScaleTransform);
            Storyboard.SetTargetProperty(scaleYOut, "ScaleY");

            var opacityOut = new DoubleAnimation
            {
                From = 1.0,
                To = 0,
                Duration = new Duration(TimeSpan.FromMilliseconds(150)),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            };
            Storyboard.SetTarget(opacityOut, StopButton);
            Storyboard.SetTargetProperty(opacityOut, "Opacity");

            var storyboardOut = new Storyboard();
            storyboardOut.Children.Add(scaleXOut);
            storyboardOut.Children.Add(scaleYOut);
            storyboardOut.Children.Add(opacityOut);
            storyboardOut.Begin();

            storyboardOut.Completed += (s, e) =>
            {
                StopButton.Visibility = Visibility.Collapsed;
                LaunchButton.Visibility = Visibility.Visible;

                LaunchScaleTransform.ScaleX = 0.5;
                LaunchScaleTransform.ScaleY = 0.5;

                var scaleXIn = new DoubleAnimation
                {
                    From = 0.5,
                    To = 1.0,
                    Duration = new Duration(TimeSpan.FromMilliseconds(150)),
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };
                Storyboard.SetTarget(scaleXIn, LaunchScaleTransform);
                Storyboard.SetTargetProperty(scaleXIn, "ScaleX");

                var scaleYIn = new DoubleAnimation
                {
                    From = 0.5,
                    To = 1.0,
                    Duration = new Duration(TimeSpan.FromMilliseconds(150)),
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };
                Storyboard.SetTarget(scaleYIn, LaunchScaleTransform);
                Storyboard.SetTargetProperty(scaleYIn, "ScaleY");

                var opacityIn = new DoubleAnimation
                {
                    From = 0,
                    To = 1,
                    Duration = new Duration(TimeSpan.FromMilliseconds(150)),
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };
                Storyboard.SetTarget(opacityIn, LaunchButton);
                Storyboard.SetTargetProperty(opacityIn, "Opacity");

                var storyboardIn = new Storyboard();
                storyboardIn.Children.Add(scaleXIn);
                storyboardIn.Children.Add(scaleYIn);
                storyboardIn.Children.Add(opacityIn);
                storyboardIn.Begin();

                storyboardIn.Completed += (s2, e2) =>
                {
                    LaunchButton.IsEnabled = true;
                    UpdateButtonStates();
                };
            };
        }
    }
}