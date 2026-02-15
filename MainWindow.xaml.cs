using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GameLauncher.Data;
using GameLauncher.Models;
using GameLauncher.Services;
using GameLauncher.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI; 
using Microsoft.UI.Windowing;
using WinRT.Interop;
using Windows.Storage;
using Windows.ApplicationModel.DataTransfer;


namespace GameLauncher
{
    public sealed partial class MainWindow : Window
    {
        private readonly DatabaseContext _dbContext;
        private readonly GameRepository _repository;
        private readonly GameService _gameService;
        private readonly ObservableCollection<Game> _games;
        private readonly ObservableCollection<Game> _filteredGames;
        private bool _isClosing = false;
        private bool _isDialogOpen = false;
        private bool _isBatchSelectionMode = false;
        private DispatcherTimer _statusCheckTimer;
        private readonly Dictionary<int, DateTime> _runningGames = new();
        private SystemTrayService _trayService;

        public ObservableCollection<Game> Games => _games;
        public ObservableCollection<Game> FilteredGames => _filteredGames;

        public MainWindow()
        {
            InitializeComponent();

            _dbContext = new DatabaseContext();
            _repository = new GameRepository(_dbContext);
            _gameService = new GameService(_repository);
            _games = new ObservableCollection<Game>();
            _filteredGames = new ObservableCollection<Game>();

            // 绑定窗口事件，确保每次激活时都刷新数据
            Activated += MainWindow_Activated;
            Closed += MainWindow_Closed;

            // 初始化定时器
            InitializeStatusCheckTimer();

            // --- 设置图标代码开始 ---
            // 1. 获取窗口的 HWND (窗口句柄)
            IntPtr hWnd = WindowNative.GetWindowHandle(this);
            WindowId windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
            AppWindow appWindow = AppWindow.GetFromWindowId(windowId);

            // 2. 设置图标 (路径要对应你项目中的图标文件)
            // 注意：如果是非打包应用，通常图标文件需要复制到输出目录
            appWindow.SetIcon("AppIcon.jpg");
            // --- 设置图标代码结束 ---

            // 初始化托盘服务
            _trayService = new SystemTrayService(this);
            _trayService.TrayIconClicked += (s, e) => _trayService.RestoreFromTray();
        }

        private void Button_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            // 阻止事件继续向父级（Border）冒泡
            e.Handled = true;
        }


        private void InitializeStatusCheckTimer()
        {
            _statusCheckTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(5)
            };
            _statusCheckTimer.Tick += StatusCheckTimer_Tick;
            _statusCheckTimer.Start();
        }

        private void StatusCheckTimer_Tick(object sender, object e)
        {
            if (_isClosing)
            {
                return;
            }
            CheckRunningGames();
            UpdateGameCardStatistics();
        }

        // 辅助：在 UI 线程安全执行 action（优先使用 Dispatcher，其次使用 DispatcherQueue，最后直接调用）
        private void RunOnUi(Action action)
        {
            try
            {
                if (Dispatcher != null)
                {
                    _ = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () => action());
                }
                else if (this.DispatcherQueue != null)
                {
                    this.DispatcherQueue.TryEnqueue(() => action());
                }
                else
                {
                    // 极端情况下没有可用的调度器，直接执行（注意：可能不是在 UI 线程）
                    action();
                }
            }
            catch
            {
                // 忽略调度错误
            }
        }

        private void CheckRunningGames()
        {
            if (_games == null || _gameService == null || _isClosing)
            {
                return;
            }

            var processes = Process.GetProcesses();
            bool hadRunningGames = _runningGames.Count > 0;

            foreach (var kvp in _runningGames.ToList())
            {
                var gameId = kvp.Key;
                var game = _games.FirstOrDefault(g => g.Id == gameId);
                if (game == null) continue;

                var processName = Path.GetFileNameWithoutExtension(game.ExecutablePath).ToLowerInvariant();
                var isRunning = processes.Any(p => p.ProcessName.ToLowerInvariant() == processName);

                if (!isRunning)
                {
                    var runTime = (long)(DateTime.UtcNow - kvp.Value).TotalSeconds;
                    
                    // 立即更新数据库
                    _ = _gameService.UpdateGamePlayTimeAsync(gameId, runTime);
                    _ = _gameService.UpdateGameRunningStatusAsync(gameId, false);
                    
                    _runningGames.Remove(gameId);
                    
                    // 更新 UI
                    if (!_isClosing)
                    {
                        RunOnUi(() =>
                        {
                            try
                            {
                                game.IsRunning = false;
                                game.TotalPlayTime += runTime;
                                // 不在此处修改 LaunchCount，避免与启动时的数据库更新重复计数
                                UpdateGameCardStatistics();
                            }
                            catch
                            {
                                // 忽略 UI 更新错误
                            }
                        });
                    }
                }
            }

            // 如果之前有正在运行的游戏，现在没有了，恢复窗口
            if (hadRunningGames && _runningGames.Count == 0)
            {
                RunOnUi(() => _trayService.RestoreFromTray());
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    var runningProcesses = processes;
                    foreach (var game in _games.Where(g => g.IsRunning))
                    {
                        if (_isClosing) break;
                        
                        var processName = Path.GetFileNameWithoutExtension(game.ExecutablePath).ToLowerInvariant();
                        var isRunning = runningProcesses.Any(p => p.ProcessName.ToLowerInvariant() == processName);
                        
                        if (!isRunning)
                        {
                            try
                            {
                                // 仅在 UI 线程设置 IsRunning=false（不要假设 Dispatcher 可用）
                                RunOnUi(() => { 
                                    game.IsRunning = false;
                                    UpdateGameCardStatistics();
                                });

                                // 然后更新数据库
                                await _gameService.UpdateGameRunningStatusAsync(game.Id, false);
                            }
                            catch
                            {
                                // 忽略调度器错误（可能是窗口已关闭）
                            }
                        }
                    }
                }
                catch
                {
                    // 忽略任务中的错误
                }
            });
        }

        private void MainWindow_Closed(object sender, WindowEventArgs args)
        {
            _isClosing = true;
            _statusCheckTimer?.Stop();
            
            // 更新所有正在运行的游戏时长
            foreach (var kvp in _runningGames.ToList())
            {
                var gameId = kvp.Key;
                var game = _games.FirstOrDefault(g => g.Id == gameId);
                if (game != null)
                {
                    var runTime = (long)(DateTime.UtcNow - kvp.Value).TotalSeconds;
                    _ = _gameService.UpdateGamePlayTimeAsync(gameId, runTime);
                    _ = _gameService.UpdateGameRunningStatusAsync(gameId, false);
                }
            }
            
            _runningGames.Clear();
            
            // 释放托盘服务资源
            _trayService?.Dispose();
        }

        private async void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
        {
            await System.Threading.Tasks.Task.Delay(100);

            try
            {
                System.Diagnostics.Debug.WriteLine("开始初始化数据库...");
                var initializer = new DatabaseInitializer(_dbContext);
                await initializer.InitializeAsync();
                System.Diagnostics.Debug.WriteLine("数据库初始化完成，开始加载游戏数据...");
                await LoadGamesAsync();
                System.Diagnostics.Debug.WriteLine("游戏数据加载完成");
            }
            catch (Microsoft.Data.Sqlite.SqliteException ex)
            {
                System.Diagnostics.Debug.WriteLine($"数据库错误: {ex.Message}");
                await ShowErrorDialog("数据库错误", $"数据库操作失败：{ex.Message}\n\n请尝试删除应用程序数据文件夹中的 games.db 文件后重新启动。");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"初始化失败: {ex.Message}");
                await ShowErrorDialog("初始化失败", $"发生错误：{ex.Message}");
            }
        }

        private async Task LoadGamesAsync()
        {
            try
            {
                var games = await _gameService.GetAllGamesAsync();
                _games.Clear();
                _filteredGames.Clear();

                foreach (var game in games)
                {
                    try
                    {
                        game.LoadIcon();
                        game.LoadImages();
                        _games.Add(game);
                        _filteredGames.Add(game);

                        if (game.IsRunning && !_runningGames.ContainsKey(game.Id))
                        {
                            _runningGames[game.Id] = DateTime.UtcNow;
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"加载游戏 {game.Name} 时出错: {ex.Message}");
                        // 继续加载其他游戏
                    }
                }

                UpdateEmptyState();
                // 延迟更新 UI，确保所有游戏卡片都已经渲染完成
                await Task.Delay(200);
                UpdateGameCardStatistics();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"加载游戏数据失败: {ex.Message}");
                throw;
            }
        }

        private void UpdateEmptyState()
        {
            if (EmptyState == null || GamesGridView == null)
            {
                return;
            }
            EmptyState.Visibility = _filteredGames.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            GamesGridView.Visibility = _filteredGames.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        private void UpdateGameCardStatistics()
        {
            // Use RunOnUi to avoid direct Dispatcher usage which may be null in some contexts.
            RunOnUi(() =>
            {
                try
                {
                    if (GamesGridView == null || _games == null)
                    {
                        return;
                    }

                    foreach (var game in _games)
                    {
                        try
                        {
                            var container = GamesGridView.ContainerFromItem(game) as GridViewItem;
                            if (container == null)
                            {
                                continue;
                            }

                            // The ContentTemplateRoot may not be a Grid; it can be a Border (the root of the DataTemplate).
                            // Use FrameworkElement and FindName so we don't assume a specific root type.
                            var root = container.ContentTemplateRoot as FrameworkElement;
                            if (root == null)
                            {
                                continue;
                            }

                            // 更新启动次数
                            var launchCountText = root.FindName("LaunchCountText") as TextBlock;
                            if (launchCountText != null)
                            {
                                launchCountText.Text = $"{game.LaunchCount}次";
                            }

                            // 更新运行时间
                            var playTimeText = root.FindName("PlayTimeText") as TextBlock;
                            if (playTimeText != null)
                            {
                                if (game.TotalPlayTime < 60)
                                {
                                    playTimeText.Text = $"{game.TotalPlayTime}秒";
                                }
                                else if (game.TotalPlayTime < 3600)
                                {
                                    var minutes = game.TotalPlayTime / 60;
                                    playTimeText.Text = $"{minutes}分钟";
                                }
                                else
                                {
                                    var hours = game.TotalPlayTime / 3600;
                                    if (hours >= 24)
                                    {
                                        var days = hours / 24;
                                        playTimeText.Text = $"{days}天";
                                    }
                                    else
                                    {
                                        playTimeText.Text = $"{hours}小时";
                                    }
                                }
                            }

                            // 更新运行状态指示器
                            var runningIndicator = root.FindName("RunningIndicator") as TextBlock;
                            if (runningIndicator != null)
                            {
                                runningIndicator.Visibility = game.IsRunning ? Visibility.Visible : Visibility.Collapsed;
                            }
                        }
                        catch
                        {
                            // 忽略单个游戏卡片的更新错误
                        }
                    }
                }
                catch
                {
                    // 忽略整体更新错误
                }
            });
        }

        private async void AddGameButton_Click(object sender, RoutedEventArgs e)
        {
            // 检查是否已经有对话框打开
            if (_isDialogOpen)
            {
                return;
            }

            try
            {
                _isDialogOpen = true;
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
                        IconPath = dialog.IconPath,
                        Description = dialog.Description
                    };

                    // 添加预览图
                    foreach (var imagePath in dialog.ImagePaths)
                    {
                        newGame.ImagePaths.Add(imagePath);
                    }

                    newGame.LoadIcon();
                    newGame.LoadImages();

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
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"添加游戏时出错: {ex.Message}");
                await ShowErrorDialog("错误", $"添加游戏时发生错误：{ex.Message}");
            }
            finally
            {
                _isDialogOpen = false;
            }
        }

        private async void LaunchButton_Click(object sender, RoutedEventArgs e)
                {
                    if (sender is Button button && button.DataContext is Game game)
                    {
                        var success = await _gameService.LaunchGameAsync(game);
                        if (success)
                        {
                            if (!_runningGames.ContainsKey(game.Id))
                            {
                                _runningGames[game.Id] = DateTime.UtcNow;
                            }
                            // 确保 UI 反映运行状态
                            RunOnUi(() => {
                                game.IsRunning = true;
                            });
                            // 延迟更新 UI，确保 IsRunning 属性已经生效
                            await Task.Delay(100);
                            UpdateGameCardStatistics();
                            // 最小化到托盘
                            _trayService.MinimizeToTray();
                        }
                        else
                        {
                            await ShowErrorDialog("启动失败", "无法启动游戏，请检查游戏路径是否正确");
                        }
                    }
                }
        private async void EditButton_Click(object sender, RoutedEventArgs e)
        {
            // 检查是否已经有对话框打开
            if (_isDialogOpen)
            {
                return;
            }

            try
            {
                _isDialogOpen = true;
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
                        game.IconPath = dialog.IconPath;
                        game.Description = dialog.Description;

                        // 更新预览图列表
                        game.ImagePaths.Clear();
                        foreach (var imagePath in dialog.ImagePaths)
                        {
                            game.ImagePaths.Add(imagePath);
                        }

                        game.LoadIcon();
                        game.LoadImages();

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
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"编辑游戏时出错: {ex.Message}");
                await ShowErrorDialog("错误", $"编辑游戏时发生错误：{ex.Message}");
            }
            finally
            {
                _isDialogOpen = false;
            }
        }

        private async void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            // 检查是否已经有对话框打开
            if (_isDialogOpen)
            {
                return;
            }

            try
            {
                _isDialogOpen = true;
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
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"删除游戏时出错: {ex.Message}");
                await ShowErrorDialog("错误", $"删除游戏时发生错误：{ex.Message}");
            }
            finally
            {
                _isDialogOpen = false;
            }
        }

        private async System.Threading.Tasks.Task ShowErrorDialog(string title, string message)
        {
            try
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
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"显示错误对话框时出错: {ex.Message}");
            }
        }

        private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
            {
                FilterGames(sender.Text);
            }
        }

        private void FilterGames(string searchText)
        {
            _filteredGames.Clear();

            if (string.IsNullOrWhiteSpace(searchText))
            {
                foreach (var game in _games)
                {
                    _filteredGames.Add(game);
                }
            }
            else
            {
                var lowerSearchText = searchText.ToLowerInvariant();
                foreach (var game in _games)
                {
                    if (game.Name.ToLowerInvariant().Contains(lowerSearchText) ||
                        (game.Description?.ToLowerInvariant().Contains(lowerSearchText) ?? false))
                    {
                        _filteredGames.Add(game);
                    }
                }
            }

            UpdateEmptyState();
        }

        private void SelectModeButton_Click(object sender, RoutedEventArgs e)
        {
            if (GamesGridView == null) return;

            _isBatchSelectionMode = true;
            GamesGridView.SelectionMode = ListViewSelectionMode.Multiple;
            GamesGridView.IsItemClickEnabled = false;
            SelectModeButton.Visibility = Visibility.Collapsed;

            if (BatchDeleteButton != null)
            {
                BatchDeleteButton.Visibility = Visibility.Visible;
            }

            if (CancelSelectButton != null)
            {
                CancelSelectButton.Visibility = Visibility.Visible;
            }

            AddGameButton.Visibility = Visibility.Collapsed;
        }

        private void CancelSelectButton_Click(object sender, RoutedEventArgs e)
        {
            // 始终在 UI 线程执行 UI 修改，避免跨线程访问引发 COMException
            RunOnUi(() =>
            {
                try
                {
                    if (GamesGridView == null) return;

                    // 在切换 SelectionMode 之前先安全地清除所选项。
                    // 有时当 SelectionMode 为 None 时访问 SelectedItems 会引发 COMException，
                    // 因此在需要时临时设置为 Multiple 来执行 Clear。
                    if (GamesGridView.SelectedItems != null)
                    {
                        if (GamesGridView.SelectionMode == ListViewSelectionMode.None)
                        {
                            GamesGridView.SelectionMode = ListViewSelectionMode.Multiple;
                        }

                        try
                        {
                            GamesGridView.SelectedItems.Clear();
                        }
                        catch
                        {
                            // 某些情况下 Clear 可能仍会失败，尝试通过 SelectedIndex 取消选择
                            try
                            {
                                GamesGridView.SelectedIndex = -1;
                            }
                            catch
                            {
                                // 忽略最终失败
                            }
                        }
                    }

                    // 现在禁用选择模式并恢复点击行为
                    GamesGridView.SelectionMode = ListViewSelectionMode.None;
                    GamesGridView.IsItemClickEnabled = true;

                    if (SelectModeButton != null)
                    {
                        SelectModeButton.Visibility = Visibility.Visible;
                    }

                    if (BatchDeleteButton != null)
                    {
                        BatchDeleteButton.Visibility = Visibility.Collapsed;
                    }

                    if (CancelSelectButton != null)
                    {
                        CancelSelectButton.Visibility = Visibility.Collapsed;
                    }

                    if (AddGameButton != null)
                    {
                        AddGameButton.Visibility = Visibility.Visible;
                    }

                    // 重置批量选择模式标志
                    _isBatchSelectionMode = false;
                }
                catch
                {
                    // 忽略批量操作取消时的错误
                }
            });
        }

        private async void BatchDeleteButton_Click(object sender, RoutedEventArgs e)
        {
            // 检查是否已经有对话框打开
            if (_isDialogOpen)
            {
                return;
            }

            if (GamesGridView == null) return;

            try
            {
                _isDialogOpen = true;

                // 按钮点击事件已经在 UI 线程，直接读取 SelectedItems
                List<int> selectedIds = new List<int>();
                if (GamesGridView.SelectedItems != null)
                {
                    selectedIds = GamesGridView.SelectedItems.Cast<Game>().Select(g => g.Id).ToList();
                }

                if (selectedIds.Count == 0)
                {
                    return;
                }

                var dialog = new ContentDialog
                {
                    Title = "确认批量删除",
                    Content = $"确定要删除选中的 {selectedIds.Count} 个游戏吗？",
                    PrimaryButtonText = "删除",
                    CloseButtonText = "取消",
                    XamlRoot = Content.XamlRoot,
                    DefaultButton = ContentDialogButton.Close
                };

                var result = await dialog.ShowAsync();

                if (result == ContentDialogResult.Primary)
                {
                    foreach (var id in selectedIds)
                    {
                        await _gameService.DeleteGameAsync(id);
                    }

                    await LoadGamesAsync();

                    // 取消选择模式
                    CancelSelectButton_Click(sender, e);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"批量删除时出错: {ex.Message}");
                await ShowErrorDialog("错误", $"批量删除时发生错误：{ex.Message}");
            }
            finally
            {
                _isDialogOpen = false;
            }
        }

        private void GamesGridView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (BatchDeleteButton == null || GamesGridView == null) return;

            BatchDeleteButton.IsEnabled = GamesGridView.SelectedItems != null && GamesGridView.SelectedItems.Count > 0;
        }

        private void GameCard_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            try
            {
                if (sender is Border border)
                {
                    // 尝试获取资源，如果失败则使用默认值
                    if (Application.Current.Resources.TryGetValue("SubtleFillColorSecondaryBrush", out var bgResource))
                    {
                        border.Background = (Microsoft.UI.Xaml.Media.Brush)bgResource;
                    }
                    else
                    {
                        border.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.LightGray);
                    }

                    if (Application.Current.Resources.TryGetValue("SystemAccentColorBrush", out var borderResource))
                    {
                        border.BorderBrush = (Microsoft.UI.Xaml.Media.Brush)borderResource;
                    }
                    else
                    {
                        border.BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Blue);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"GameCard_PointerEntered 出错: {ex.Message}");
            }
        }

        private void GameCard_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            try
            {
                if (sender is Border border)
                {
                    // 尝试获取资源，如果失败则使用默认值
                    if (Application.Current.Resources.TryGetValue("CardBackgroundFillColorDefaultBrush", out var bgResource))
                    {
                        border.Background = (Microsoft.UI.Xaml.Media.Brush)bgResource;
                    }
                    else
                    {
                        border.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White);
                    }

                    if (Application.Current.Resources.TryGetValue("CardStrokeColorDefaultBrush", out var borderResource))
                    {
                        border.BorderBrush = (Microsoft.UI.Xaml.Media.Brush)borderResource;
                    }
                    else
                    {
                        border.BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.LightGray);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"GameCard_PointerExited 出错: {ex.Message}");
            }
        }

    private async void GameCard_PointerPressed(
        object sender,
        PointerRoutedEventArgs e)
    {
        // 如果事件已经被按钮处理过，直接退出
        if (e.Handled)
        { 
            return; 
        }
        // 防止重复弹窗
        if (_isDialogOpen || _isBatchSelectionMode)
        {
            return;
        }

        try
        {
            if (sender is Border border && border.DataContext is Game game)
            {
                _isDialogOpen = true;

                var detailDialog = new Views.GameDetailDialog(game)
                {
                    XamlRoot = Content.XamlRoot
                };

                await detailDialog.ShowAsync();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"打开游戏详情时出错: {ex.Message}");
            await ShowErrorDialog("错误", $"打开游戏详情时发生错误：{ex.Message}");
        }
        finally
        {
            _isDialogOpen = false;
            }
        }

        private void GamesGridView_DragOver(object sender, DragEventArgs e)
        {
            // 只接受文件
            if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                e.AcceptedOperation = DataPackageOperation.Copy;
                e.DragUIOverride.Caption = "释放以添加游戏";
            }
            else
            {
                e.AcceptedOperation = DataPackageOperation.None;
            }
        }

        private async void GamesGridView_Drop(object sender, DragEventArgs e)
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                return;
            }

            try
            {
                var items = await e.DataView.GetStorageItemsAsync();
                if (items == null || items.Count == 0u)
                {
                    return;
                }

                foreach (var item in items)
                {
                    if (item is IStorageFile file)
                    {
                        var extension = Path.GetExtension(file.Path).ToLowerInvariant();
                        
                        // 只支持.exe、.bat和.lnk文件
                        if (extension == ".exe" || extension == ".bat" || extension == ".lnk")
                        {
                            await AddGameFromDragDrop(file.Path);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"拖放添加游戏时出错: {ex.Message}");
                await ShowErrorDialog("错误", $"添加游戏时发生错误：{ex.Message}");
            }
        }

        private async System.Threading.Tasks.Task AddGameFromDragDrop(string filePath)
        {
            if (_isDialogOpen)
            {
                return;
            }

            try
            {
                _isDialogOpen = true;
                
                // 检查是否已存在相同路径的游戏
                var existingGame = _games.FirstOrDefault(g => 
                    string.Equals(g.ExecutablePath, filePath, StringComparison.OrdinalIgnoreCase));
                
                if (existingGame != null)
                {
                    var infoDialog = new ContentDialog
                    {
                        Title = "游戏已存在",
                        Content = $"游戏「{existingGame.Name}」已经存在于库中",
                        CloseButtonText = "确定",
                        XamlRoot = Content.XamlRoot
                    };
                    await infoDialog.ShowAsync();
                    return;
                }

                var fileName = Path.GetFileNameWithoutExtension(filePath);
                var newGame = new Game
                {
                    Name = fileName,
                    ExecutablePath = filePath,
                    IconPath = string.Empty,
                    Description = string.Empty
                };

                newGame.LoadIcon();
                newGame.LoadImages();

                try
                {
                    await _gameService.AddGameAsync(newGame);
                    await LoadGamesAsync();
                    
                    var successDialog = new ContentDialog
                    {
                        Title = "添加成功",
                        Content = $"已成功添加游戏「{fileName}」",
                        CloseButtonText = "确定",
                        XamlRoot = Content.XamlRoot
                    };
                    await successDialog.ShowAsync();
                }
                catch (Exception ex)
                {
                    await ShowErrorDialog("添加游戏失败", ex.Message);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"从拖放添加游戏时出错: {ex.Message}");
                await ShowErrorDialog("错误", $"添加游戏时发生错误：{ex.Message}");
            }
            finally
            {
                _isDialogOpen = false;
            }
        }
    }
}
