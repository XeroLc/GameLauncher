﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GameLauncher.Data;
using GameLauncher.Models;
using GameLauncher.Services;
using GameLauncher.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Dispatching;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using WinRT.Interop;
using Windows.Storage;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;


namespace GameLauncher
{
    public sealed partial class MainWindow : Window
    {
        private readonly DatabaseContext _dbContext;
        private readonly GameRepository _repository;
        private readonly GameService _gameService;
        private readonly ObservableCollection<Game> _games;
        private readonly ObservableCollection<string> _allTags;
        private readonly DataSyncService _syncService;
        private volatile bool _isDialogOpen = false;
        private static bool _isShowingUpdateDialog = false;
        private bool _isBatchSelectionMode = false;
        private SystemTrayService _trayService;
        private SolidColorBrush _navBarBackgroundBrush = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(140, 249, 249, 249));
        private bool _isNavBarScrolled = false;
        private bool _isFirstActivation = true;
        private volatile int _isActivatedHandling = 0;
        private readonly SolidColorBrush _hoverBorderBrush = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(80, 255, 255, 255));
        private readonly UpdateCheckerService _updateChecker;
        private readonly ImageService _imageService;
        private readonly GmdFileService _gmdService;
        private readonly GameImageLoader _gameImageLoader;

        public ObservableCollection<Game> Games => _games;
        public ObservableCollection<Game> FilteredGames => _filteredGames;

        public MainWindow()
        {
            InitializeComponent();

            Content.KeyDown += MainWindow_KeyDown;

            SetupFloatingNavBar();

            _dbContext = App.Services.GetRequiredService<DatabaseContext>();
            _repository = App.Services.GetRequiredService<GameRepository>();
            _gameService = App.Services.GetRequiredService<GameService>();
            _games = new ObservableCollection<Game>();
            _filteredGames = new ObservableCollection<Game>();
            _allTags = new ObservableCollection<string>();
            _syncService = App.Services.GetRequiredService<DataSyncService>();
            _updateChecker = App.Services.GetRequiredService<UpdateCheckerService>();
            _imageService = App.Services.GetRequiredService<ImageService>();
            _gmdService = App.Services.GetRequiredService<GmdFileService>();
            _gameImageLoader = App.Services.GetRequiredService<GameImageLoader>();

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
            appWindow.SetIcon("AppIcon.ico");
            // --- 设置图标代码结束 ---

            // 初始化托盘服务
            _trayService = new SystemTrayService(this);
            _trayService.TrayIconClicked += (s, e) => _trayService.RestoreFromTray();
        }

        private void Button_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            e.Handled = true;
        }

        private void SetupFloatingNavBar()
        {
            NavBar.Background = _navBarBackgroundBrush;

            try
            {
                NavBar.Translation = new System.Numerics.Vector3(0, 0, 8);
                var shadow = new Microsoft.UI.Xaml.Media.ThemeShadow();
                NavBar.Shadow = shadow;
            }
            catch
            {
            }

            NavBar.SizeChanged += (s, e) =>
            {
                var navBarTotalHeight = NavBar.ActualHeight + 16;
                GamesGridView.Margin = new Thickness(0, navBarTotalHeight, 0, 0);
            };

            GamesGridView.Loaded += (s, e) =>
            {
                var scrollViewer = FindVisualChild<ScrollViewer>(GamesGridView);
                if (scrollViewer != null)
                {
                    scrollViewer.ViewChanged += GridViewScrollViewer_ViewChanged;
                }
            };
        }

        private void GridViewScrollViewer_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
        {
            if (sender is not ScrollViewer scrollViewer) return;
            var scrollOffset = scrollViewer.VerticalOffset;
            var shouldScroll = scrollOffset > 10;

            if (shouldScroll != _isNavBarScrolled)
            {
                _isNavBarScrolled = shouldScroll;

                var targetOpacity = shouldScroll ? 0.88 : 0.55;

                var storyboard = new Storyboard();
                var animation = new DoubleAnimation
                {
                    From = _navBarBackgroundBrush.Opacity,
                    To = targetOpacity,
                    Duration = new Duration(TimeSpan.FromMilliseconds(250)),
                    EasingFunction = new QuadraticEase()
                };

                Storyboard.SetTarget(animation, _navBarBackgroundBrush);
                Storyboard.SetTargetProperty(animation, "Opacity");

                storyboard.Children.Add(animation);
                storyboard.Begin();
            }
        }

        private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is T result)
                    return result;
                var found = FindVisualChild<T>(child);
                if (found != null)
                    return found;
            }
            return null;
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
                    System.Diagnostics.Debug.WriteLine("RunOnUi: 无可用的UI调度器，跳过操作");
                }
            }
            catch
            {
                // 忽略调度错误
            }
        }

        private async void MainWindow_Closed(object sender, WindowEventArgs args)
        {
            _isClosing = true;
            _statusCheckTimer?.Stop();
            
            var gamesToClose = _runningGames.ToList();
            _runningGames.Clear();
            
            foreach (var kvp in gamesToClose)
            {
                var gameId = kvp.Key;
                var game = _games.FirstOrDefault(g => g.Id == gameId);
                if (game != null)
                {
                    var runTime = (long)(DateTime.UtcNow - kvp.Value).TotalSeconds;
                    game.TotalPlayTime += runTime;
                    game.IsRunning = false;
                    
                    try
                    {
                        await _gameService.UpdateGamePlayTimeAsync(gameId, runTime);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"关闭窗口时更新游戏时长失败: {ex.Message}");
                    }
                }
            }
            
            _trayService?.Dispose();
        }

        private async void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
        {
            if (Interlocked.Exchange(ref _isActivatedHandling, 1) == 1)
            {
                return;
            }

            try
            {
                await System.Threading.Tasks.Task.Delay(100);

                try
                {
                    System.Diagnostics.Debug.WriteLine("开始初始化数据库...");
                    await _dbContext.InitializeAsync();
                    System.Diagnostics.Debug.WriteLine("数据库初始化完成，开始加载游戏数据...");

                    if (_isFirstActivation)
                    {
                        _isFirstActivation = false;
                        LoadingOverlay.Visibility = Visibility.Visible;
                        await LoadGamesAsync();
                        LoadingOverlay.Visibility = Visibility.Collapsed;

                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                var settings = UserSettings.Instance;
                                if (settings.AutoScanEnabled && settings.ScanPaths.Count > 0)
                                {
                                    Debug.WriteLine("[AutoScan] 开始静默扫描...");
                                    var scanService = App.Services.GetRequiredService<AutoScanService>();
                                    var result = await scanService.ScanAsync(settings.ScanPaths);
                                    if (result.NewGamesFound > 0)
                                    {
                                        int imported = await _gameService.ImportGamesAsync(result.DiscoveredGames);
                                        RunOnUi(() => {
                                            ShowToast("自动扫描", $"发现 {result.NewGamesFound} 个新游戏，已自动导入 {imported} 个。", ToastType.Success);
                                            _ = SilentRefreshGamesAsync(forceUiUpdate: true);
                                        });
                                    }
                                    Debug.WriteLine($"[AutoScan] 扫描完成: 扫描 {result.TotalScanned} 个文件, 发现 {result.NewGamesFound} 个新游戏");
                                }
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"[AutoScan] 扫描失败: {ex.Message}");
                            }
                        });

                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await Task.Delay(2000);
                                var updateInfo = await _updateChecker.CheckForUpdateAsync();
                                if (updateInfo != null)
                                {
                                    RunOnUi(() => ShowUpdateAvailableDialog(updateInfo));
                                }
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"[UpdateCheck] 自动检查更新失败: {ex.Message}");
                            }
                        });
                    }
                    else
                    {
                        await SilentRefreshGamesAsync();
                    }

                    System.Diagnostics.Debug.WriteLine("游戏数据加载完成");
                }
                catch (Microsoft.Data.Sqlite.SqliteException ex)
                {
                    System.Diagnostics.Debug.WriteLine($"数据库错误: {ex.Message}");
                    ShowToast("数据库错误", $"数据库操作失败：{ex.Message}\n\n请尝试删除应用程序数据文件夹中的 games.db 文件后重新启动。", ToastType.Error);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"初始化失败: {ex.Message}");
                    ShowToast("初始化失败", $"发生错误：{ex.Message}", ToastType.Error);
                }
            }
            finally
            {
                Interlocked.Exchange(ref _isActivatedHandling, 0);
            }
        }

        /// <summary>
        /// 静默刷新 — 仅在检测到数据变更时才执行更新
        /// 不会清空现有数据，避免界面闪烁
        /// </summary>
        private async System.Threading.Tasks.Task<SyncSummary> SilentRefreshGamesAsync(bool forceUiUpdate = false)
        {
            try
            {
                var summary = await _syncService.SyncAsync(
                    existingGames: _games,
                    fetchLatestGames: async () =>
                    {
                        var games = await _gameService.GetAllGamesAsync();
                        await _gameService.PopulateGameCollectionsAsync(games);
                        return games;
                    },
                    applyAdd: (game) =>
                    {
                        _gameImageLoader.LoadIcon(game);
                        _gameImageLoader.LoadImages(game);
                        _games.Add(game);
                        AddTagsFromGame(game);
                    },
                    applyModify: (game, changedFields) =>
                    {
                        var existingGame = _games.FirstOrDefault(g => g.Id == game.Id);
                        if (existingGame != null)
                        {
                            ApplyFieldChanges(existingGame, game, changedFields);
                        }
                    },
                    applyDelete: (gameId) =>
                    {
                        var gameToRemove = _games.FirstOrDefault(g => g.Id == gameId);
                        if (gameToRemove != null)
                        {
                            _games.Remove(gameToRemove);
                        }
                    }
                );

                if (summary.HasChanges || forceUiUpdate)
                {
                    RunOnUi(() =>
                    {
                        ApplyFilters();
                        UpdateEmptyState();
                        UpdateGameCardStatistics();
                    });
                }

                return summary;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"静默刷新失败: {ex.Message}");
                return new SyncSummary { HasChanges = false, Description = $"异常: {ex.Message}" };
            }
        }

        /// <summary>
        /// 将游戏的标签添加到全局标签集合中
        /// </summary>
        private void AddTagsFromGame(Game game)
        {
            foreach (var tag in game.Tags)
            {
                if (!_allTags.Contains(tag))
                {
                    _allTags.Add(tag);
                }
            }

            var sortedTags = _allTags.OrderBy(t => t).ToList();
            _allTags.Clear();
            foreach (var tag in sortedTags)
            {
                _allTags.Add(tag);
            }

            UpdateTagFilterComboBox();
        }

        /// <summary>
        /// 根据变更的字段列表，将最新数据应用到现有游戏对象
        /// </summary>
        private void ApplyFieldChanges(Game target, Game source, IEnumerable<string> changedFields)
        {
            foreach (var field in changedFields)
            {
                try
                {
                    switch (field)
                    {
                        case nameof(Game.Name):
                            target.Name = source.Name;
                            break;
                        case nameof(Game.ExecutablePath):
                            target.ExecutablePath = source.ExecutablePath;
                            break;
                        case nameof(Game.Description):
                            target.Description = source.Description;
                            break;
                        case nameof(Game.LaunchCount):
                            target.LaunchCount = source.LaunchCount;
                            break;
                        case nameof(Game.TotalPlayTime):
                            target.TotalPlayTime = source.TotalPlayTime;
                            break;
                        case nameof(Game.IsRunning):
                            target.IsRunning = source.IsRunning;
                            break;
                        case nameof(Game.LastRunTime):
                            target.LastRunTime = source.LastRunTime;
                            break;
                        case nameof(Game.IconPath):
                            target.IconPath = source.IconPath;
                            _gameImageLoader.LoadIcon(target);
                            break;
                        case nameof(Game.Tags):
                            target.Tags.Clear();
                            foreach (var tag in source.Tags)
                            {
                                target.Tags.Add(tag);
                            }
                            break;
                        case nameof(Game.ImagePaths):
                            target.ImagePaths.Clear();
                            foreach (var path in source.ImagePaths)
                            {
                                target.ImagePaths.Add(path);
                            }
                            break;
                        case nameof(Game.Collections):
                            target.Collections.Clear();
                            foreach (var col in source.Collections)
                            {
                                if (!target.Collections.Any(c => c.Id == col.Id))
                                    target.Collections.Add(col);
                            }
                            break;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"应用字段变更 {field} 失败: {ex.Message}");
                }
            }
        }

        private async Task LoadGamesAsync()
        {
            try
            {
                var games = await _gameService.GetAllGamesAsync();

                _games.Clear();
                _filteredGames.Clear();
                _allTags.Clear();

                var uniqueTags = new HashSet<string>();
                foreach (var game in games)
                {
                    foreach (var tag in game.Tags)
                        uniqueTags.Add(tag);
                }
                var sortedTags = uniqueTags.OrderBy(t => t).ToList();
                foreach (var tag in sortedTags)
                    _allTags.Add(tag);
                UpdateTagFilterComboBox();

                foreach (var game in games)
                {
                    _games.Add(game);
                    _filteredGames.Add(game);
                    if (game.IsRunning && !_runningGames.ContainsKey(game.Id))
                        _runningGames[game.Id] = DateTime.UtcNow;
                }

                await _gameService.PopulateGameCollectionsAsync(games);
                ApplyFilters();
                await RefreshCollectionFilterAsync();
                UpdateEmptyState();

                var dispatcher = DispatcherQueue;
                _ = Task.Run(() =>
                {
                    foreach (var game in games)
                    {
                        var capturedGame = game;
                        dispatcher.TryEnqueue(() =>
                        {
                            try { _gameImageLoader.LoadIcon(capturedGame); }
                            catch { }
                        });
                    }
                });

                _ = Task.Run(async () =>
                {
                    await RunBackgroundMaintenanceAsync(games);
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"加载游戏数据失败: {ex.Message}");
                throw;
            }
        }

        private async Task RunBackgroundMaintenanceAsync(List<Game> games)
        {
            var migrationService = App.Services.GetRequiredService<DataMigrationService>();
            var consistencyService = App.Services.GetRequiredService<DataConsistencyService>();
            bool needsRefresh = false;

            try
            {
                if (!await migrationService.IsMigrationCompletedAsync("GidAssignment"))
                {
                    var assignedCount = await migrationService.AssignGameIdsToExistingGamesAsync();
                    if (assignedCount > 0) needsRefresh = true;
                    await migrationService.MarkMigrationCompletedAsync("GidAssignment");
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[后台] GID分配: {ex.Message}"); }

            try
            {
                if (!await migrationService.IsMigrationCompletedAsync("GmdScan"))
                {
                    var missingGmdGames = await migrationService.ScanForMissingGmdFilesAsync(games);
                    if (missingGmdGames.Count > 0)
                    {
                        await migrationService.MigrateAllGamesAsync(games);
                        needsRefresh = true;
                    }
                    await migrationService.MarkMigrationCompletedAsync("GmdScan");
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[后台] GMD迁移: {ex.Message}"); }

            try
            {
                if (!await migrationService.IsMigrationCompletedAsync("ImageMigration"))
                {
                    var imageMigrationStatus = await migrationService.MigrateGameImagesToGlobalDirectoryAsync(games);
                    if (imageMigrationStatus.MigratedGames > 0) needsRefresh = true;
                    await migrationService.MarkMigrationCompletedAsync("ImageMigration");
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[后台] 图片迁移: {ex.Message}"); }

            try
            {
                if (!await migrationService.IsMigrationCompletedAsync("DirectoryClean"))
                {
                    await migrationService.CleanOldImageDirectoriesAsync(games);
                    await migrationService.MarkMigrationCompletedAsync("DirectoryClean");
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[后台] 目录清理: {ex.Message}"); }

            try
            {
                var report = await consistencyService.CheckModifiedGamesConsistencyAsync(games);
                if (report.InconsistentGames > 0)
                {
                    foreach (var detail in report.Details.Where(d => !d.IsConsistent))
                    {
                        var game = games.FirstOrDefault(g => g.Id == detail.GameId);
                        if (game != null && !string.IsNullOrEmpty(game.GmdFilePath))
                        {
                            await consistencyService.ResolveConflictAsync(game, game.GmdFilePath);
                        }
                    }
                    needsRefresh = true;
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[后台] 一致性校验: {ex.Message}"); }

            try
            {
                var fallbackGames = games.Where(g => g.NeedsGmdFallback && g.IsGmdFileReady).ToList();
                if (fallbackGames.Count > 0)
                {
                    var gmdService = App.Services.GetRequiredService<GmdFileService>();
                    foreach (var game in fallbackGames)
                    {
                        try
                        {
                            var gmdGame = await gmdService.DeserializeGameFromGmdAsync(game.GmdFilePath);
                            if (gmdGame == null) continue;

                            bool changed = false;
                            if (string.IsNullOrEmpty(game.Description) && !string.IsNullOrEmpty(gmdGame.Description))
                            { game.Description = gmdGame.Description; changed = true; }
                            if (string.IsNullOrEmpty(game.IconPath) && !string.IsNullOrEmpty(gmdGame.IconPath))
                            { game.IconPath = gmdGame.IconPath; changed = true; }
                            if (game.ImagePaths.Count == 0 && gmdGame.ImagePaths.Count > 0)
                            { foreach (var p in gmdGame.ImagePaths) game.ImagePaths.Add(p); changed = true; }
                            if (game.Tags.Count == 0 && gmdGame.Tags.Count > 0)
                            { foreach (var t in gmdGame.Tags) game.Tags.Add(t); changed = true; }

                            if (changed)
                            {
                                await _gameService.UpdateGameAsync(game);
                                game.NeedsGmdFallback = false;
                                needsRefresh = true;
                            }
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex) { Debug.WriteLine($"[后台] GMD回退处理: {ex.Message}"); }

            if (needsRefresh)
            {
                RunOnUi(async () => await SilentRefreshGamesAsync(forceUiUpdate: true));
            }
        }

        private void UpdateTagFilterComboBox()
        {
            if (TagFilterComboBox == null) return;

            TagFilterComboBox.Items.Clear();

            // 添加"全部标签"选项
            TagFilterComboBox.Items.Add("全部标签");

            // 添加所有标签
            foreach (var tag in _allTags)
            {
                TagFilterComboBox.Items.Add(tag);
            }

            // 恢复之前选择的筛选
            if (_selectedTagFilter != null)
            {
                var index = TagFilterComboBox.Items.IndexOf(_selectedTagFilter);
                if (index >= 0)
                {
                    TagFilterComboBox.SelectedIndex = index;
                }
                else
                {
                    TagFilterComboBox.SelectedIndex = 0;
                }
            }
            else
            {
                TagFilterComboBox.SelectedIndex = 0;
            }
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

                // 设置已有标签
                dialog.SetExistingTags(_allTags.ToList());

                var collections = await _gameService.GetAllCollectionsAsync();
                dialog.SetCollections(collections);

                var result = await dialog.ShowAsync();

                if (result == ContentDialogResult.Primary || dialog.IsGmdQuickImport)
                {
                    if (dialog.IsGmdQuickImport && dialog.ImportedGame != null)
                    {
                        // .gmd 快速导入：使用统一的 _gameService 保存到数据库，避免多连接冲突
                        var importedGame = dialog.ImportedGame;
                        LoadingOverlay.Visibility = Visibility.Visible;

                        try
                        {
                            // 检查 GID 是否已存在
                            if (!string.IsNullOrWhiteSpace(importedGame.GameId) && await _gameService.GameIdExistsAsync(importedGame.GameId))
                            {
                                ShowToast("提示", $"游戏「{importedGame.Name}」已存在于数据库中，无需重复添加。", ToastType.Warning);
                                LoadingOverlay.Visibility = Visibility.Collapsed;
                            }
                            else
                            {
                                // 确保图片目录已创建
                                var imageService = _imageService;
                                imageService.EnsureGameImageDirectory(importedGame.GameId);

                                // 使用统一的 _gameService 保存到数据库
                                var gameId = await _gameService.AddGameAsync(importedGame);
                                foreach (var colId in dialog.SelectedCollectionIds)
                                {
                                    await _gameService.AddGameToCollectionAsync(gameId, colId);
                                }

                                if (importedGame.Collections != null && importedGame.Collections.Count > 0)
                                {
                                    var allCollections = await _gameService.GetAllCollectionsAsync();
                                    var existingNames = allCollections.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                                    foreach (var col in importedGame.Collections.ToList())
                                    {
                                        if (string.IsNullOrWhiteSpace(col.Name)) continue;
                                        int colId;
                                        var existing = allCollections.FirstOrDefault(c => string.Equals(c.Name, col.Name, StringComparison.OrdinalIgnoreCase));
                                        if (existing != null)
                                        {
                                            colId = existing.Id;
                                        }
                                        else
                                        {
                                            var newCol = await _gameService.AddCollectionAsync(col.Name);
                                            allCollections.Add(newCol);
                                            colId = newCol.Id;
                                        }
                                        await _gameService.AddGameToCollectionAsync(gameId, colId);
                                    }
                                }

                                _gameImageLoader.ReloadIcon(importedGame);
                                _gameImageLoader.ReloadImages(importedGame);

                                LoadingOverlay.Visibility = Visibility.Collapsed;
                                await SilentRefreshGamesAsync(forceUiUpdate: true);
                            }
                        }
                        catch (Exception ex)
                        {
                            LoadingOverlay.Visibility = Visibility.Collapsed;
                            ShowToast("添加游戏失败", ex.Message, ToastType.Error);
                        }
                    }
                    else
                    {
                        // 手动添加游戏
                        var newGame = new Game
                        {
                            Name = dialog.GameName,
                            ExecutablePath = dialog.ExecutablePath,
                            Description = dialog.Description
                        };

                        // 添加标签
                        foreach (var tag in dialog.Tags)
                        {
                            newGame.Tags.Add(tag);
                        }

                        _gameImageLoader.LoadIcon(newGame);
                        _gameImageLoader.LoadImages(newGame);

                        LoadingOverlay.Visibility = Visibility.Visible;

                        try
                        {
                            // 先保存到数据库，获取 GameId
                            var gameId = await _gameService.AddGameAsync(newGame);
                            foreach (var colId in dialog.SelectedCollectionIds)
                            {
                                await _gameService.AddGameToCollectionAsync(gameId, colId);
                            }

                            // GameId 已由数据库分配，现在保存图片到全局目录
                            var imageService = _imageService;
                            imageService.EnsureGameImageDirectory(newGame.GameId);
                            bool needsUpdate = false;

                            if (!string.IsNullOrEmpty(dialog.IconPath))
                            {
                                var savedIcon = await imageService.SaveIconAsync(newGame.GameId, dialog.IconPath);
                                if (!string.IsNullOrEmpty(savedIcon))
                                {
                                    newGame.IconPath = savedIcon;
                                    needsUpdate = true;
                                }
                            }
                            else
                            {
                                var defaultIconPath = imageService.GetIconPath(newGame.GameId);
                                if (!System.IO.File.Exists(defaultIconPath))
                                {
                                    newGame.IconPath = string.Empty;
                                }
                            }

                            int previewIndex = 1;
                            foreach (var imagePath in dialog.ImagePaths)
                            {
                                var savedImage = await imageService.SavePreviewImageAsync(newGame.GameId, imagePath, previewIndex);
                                if (!string.IsNullOrEmpty(savedImage))
                                {
                                    newGame.ImagePaths.Add(savedImage);
                                    previewIndex++;
                                    needsUpdate = true;
                                }
                            }

                            // 更新数据库中的图片路径
                            if (needsUpdate)
                            {
                                await _gameService.UpdateGameAsync(newGame);
                            }

                            _gameImageLoader.ReloadIcon(newGame);
                            _gameImageLoader.ReloadImages(newGame);

                            LoadingOverlay.Visibility = Visibility.Collapsed;
                            await SilentRefreshGamesAsync(forceUiUpdate: true);
                        }
                        catch (Exception ex)
                        {
                            LoadingOverlay.Visibility = Visibility.Collapsed;
                            ShowToast("添加游戏失败", ex.Message, ToastType.Error);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"添加游戏时出错: {ex.Message}");
                ShowToast("错误", $"添加游戏时发生错误：{ex.Message}", ToastType.Error);
            }
            finally
            {
                _isDialogOpen = false;
            }
        }

        private async void StopButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.DataContext is Game game)
            {
                DateTime? startTime = null;
                if (_runningGames.ContainsKey(game.Id))
                {
                    startTime = _runningGames[game.Id];
                    _runningGames.TryRemove(game.Id, out _);
                }

                var success = await _gameService.StopGameAsync(game, startTime);
                if (success)
                {
                    RunOnUi(() => {
                        game.IsRunning = false;
                    });
                    await Task.Delay(100);
                    UpdateGameCardStatistics();
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
                    var dialog = new AddGameDialog(game, _imageService)
                    {
                        XamlRoot = Content.XamlRoot
                    };

                    // 设置已有标签
                    dialog.SetExistingTags(_allTags.ToList());

                    var collections = await _gameService.GetAllCollectionsAsync();
                    var gameCollections = await _gameService.GetCollectionsForGameAsync(game.Id);
                    dialog.SetCollections(collections, gameCollections.Select(c => c.Id).ToList());

                    var result = await dialog.ShowAsync();

                    if (result == ContentDialogResult.Primary)
                    {
                        await _gameService.UpdateGameFromDialogAsync(game, dialog, _imageService, () =>
                        {
                            _gameImageLoader.ReloadIcon(game);
                            _gameImageLoader.ReloadImages(game);
                        });
                        await SilentRefreshGamesAsync(forceUiUpdate: true);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"编辑游戏时出错: {ex.Message}");
                ShowToast("编辑失败", $"编辑游戏时发生错误：{ex.Message}", ToastType.Error);
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
                    var (confirmed, deleteGmd) = await ShowDeleteConfirmDialog(game.Name);
                    if (confirmed)
                    {
                        var success = await _gameService.DeleteGameAsync(game.Id, deleteGmd);
                        if (success)
                        {
                            await SilentRefreshGamesAsync(forceUiUpdate: true);
                        }
                        else
                        {
                            RunOnUi(() => ShowToast("删除失败", "删除游戏时发生错误", ToastType.Error));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"删除游戏时出错: {ex.Message}");
                ShowToast("删除失败", $"删除游戏时发生错误：{ex.Message}", ToastType.Error);
            }
            finally
            {
                _isDialogOpen = false;
            }
        }

        private async Task<(bool confirmed, bool deleteGmd)> ShowDeleteConfirmDialog(string gameName)
        {
            try
            {
                var deleteGmdCheckBox = new Microsoft.UI.Xaml.Controls.CheckBox
                {
                    Content = "同时删除 .gmd 文件",
                    IsChecked = true,
                    Margin = new Microsoft.UI.Xaml.Thickness(0, 12, 0, 0)
                };

                var panel = new StackPanel();
                panel.Children.Add(new TextBlock { Text = $"确定要删除游戏「{gameName}」吗？", TextWrapping = TextWrapping.Wrap });
                panel.Children.Add(deleteGmdCheckBox);

                var dialog = new ContentDialog
                {
                    Title = "确认删除",
                    Content = panel,
                    PrimaryButtonText = "删除",
                    CloseButtonText = "取消",
                    XamlRoot = Content.XamlRoot,
                    Style = (Style)App.Current.Resources["DefaultContentDialogStyle"]
                };

                var result = await dialog.ShowAsync();
                return (result == ContentDialogResult.Primary, deleteGmdCheckBox.IsChecked == true);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"显示删除确认对话框出错: {ex.Message}");
                return (false, false);
            }
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

                var (confirmed, deleteGmd) = await ShowDeleteConfirmDialog($"选中的 {selectedIds.Count} 个游戏");
                if (confirmed)
                {
                    await _gameService.DeleteGamesAsync(selectedIds, deleteGmd);
                    await SilentRefreshGamesAsync(forceUiUpdate: true);
                    RunOnUi(() => CancelSelectButton_Click(sender, e));
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"批量删除时出错: {ex.Message}");
                ShowToast("批量删除失败", $"批量删除时发生错误：{ex.Message}", ToastType.Error);
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
                    border.Background = (Microsoft.UI.Xaml.Media.Brush)App.Current.Resources["FrostedGlassCardHoverBrush"];
                    border.BorderBrush = _hoverBorderBrush;
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
                    border.Background = (Microsoft.UI.Xaml.Media.Brush)App.Current.Resources["FrostedGlassCardBrush"];
                    border.BorderBrush = (Microsoft.UI.Xaml.Media.Brush)App.Current.Resources["FrostBorderBrush"];
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
        if (e.Handled)
        { 
            return; 
        }
        if (_isDialogOpen || _isBatchSelectionMode)
        {
            return;
        }

        try
        {
            if (sender is Border border && border.DataContext is Game game)
            {
                var point = e.GetCurrentPoint(sender as UIElement);
                if (point.Properties.IsRightButtonPressed)
                {
                    e.Handled = true;
                    await ShowGameContextMenu(game, border);
                    return;
                }

                _isDialogOpen = true;

                DateTime? startTime = null;
                if (_runningGames.ContainsKey(game.Id))
                {
                    startTime = _runningGames[game.Id];
                }

                var detailDialog = new Views.GameDetailDialog(game, _gameService, _gameImageLoader, _allTags.ToList(), startTime)
                {
                    XamlRoot = Content.XamlRoot
                };

                detailDialog.GameLaunched += (launchedGame, launchTime) =>
                {
                    if (!_runningGames.ContainsKey(launchedGame.Id))
                    {
                        _runningGames[launchedGame.Id] = launchTime;
                    }
                    _trayService.MinimizeToTray();
                };

                detailDialog.GameStopped += (stoppedGame) =>
                {
                    _runningGames.TryRemove(stoppedGame.Id, out _);
                    RunOnUi(() => UpdateGameCardStatistics());
                };

                detailDialog.ShowToastRequested += (title, message) =>
                {
                    RunOnUi(() => ShowToast(title, message, ToastType.Error));
                };

                detailDialog.DataChanged += () =>
                {
                    RunOnUi(() => ApplyFilters());
                    RunOnUi(async () => await RefreshCollectionFilterAsync());
                };

                await detailDialog.ShowAsync();

                if (detailDialog.DeleteRequested)
                {
                    var (confirmed, deleteGmd) = await ShowDeleteConfirmDialog(game.Name);
                    if (confirmed)
                    {
                        var success = await _gameService.DeleteGameAsync(game.Id, deleteGmd);
                        RunOnUi(async () =>
                        {
                            if (success)
                            {
                                ShowToast("删除成功", $"游戏「{game.Name}」已删除", ToastType.Success);
                                await SilentRefreshGamesAsync(forceUiUpdate: true);
                            }
                            else
                            {
                                ShowToast("删除失败", "删除游戏时发生错误", ToastType.Error);
                            }
                        });
                    }
                }

                RunOnUi(() => UpdateGameCardStatistics());
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"打开游戏详情时出错: {ex.Message}");
            ShowToast("错误", $"打开游戏详情时发生错误：{ex.Message}", ToastType.Error);
        }
        finally
        {
            _isDialogOpen = false;
        }
    }

        private void SortComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SortComboBox == null || SortComboBox.SelectedItem == null || _filteredGames == null || _games == null) return;

            var selectedItem = SortComboBox.SelectedItem as ComboBoxItem;
            if (selectedItem != null && selectedItem.Tag is string tag)
            {
                _currentSortMode = tag;
                ApplyFilters();
            }
        }

        private async void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isDialogOpen)
            {
                return;
            }

            try
            {
                _isDialogOpen = true;
                var dialog = new Views.SettingsDialog()
                {
                    XamlRoot = Content.XamlRoot
                };

                await dialog.ShowAsync();
                RunOnUi(() => ApplyFilters());
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"打开设置时出错: {ex.Message}");
            }
            finally
            {
                _isDialogOpen = false;
            }
        }

        private void VersionWatermark_Click(object sender, RoutedEventArgs e)
        {
            ShowChangelogDialog();
        }
        private async void ShowChangelogDialog()
        {
            var sb = new System.Text.StringBuilder();
            var sep = "----------------------------------";
            sb.AppendLine("v3.3 (2026-07-20)");
            sb.AppendLine(sep);
            sb.AppendLine("  功能新增");
            sb.AppendLine("    游戏列表支持分页（10/15/30/50/100 条每页）");
            sb.AppendLine("    游戏详情页新增「打开游戏路径」按钮");
            sb.AppendLine("    添加 Ctrl+K 快捷键聚焦搜索框");
            sb.AppendLine("    搜索升级为模糊搜索，支持拼音首字母匹配");
            sb.AppendLine("    删除确认弹窗新增「同时删除 .gmd 文件」勾选项");
            sb.AppendLine();
            sb.AppendLine("  体验优化");
            sb.AppendLine("    所有提示/确认对话框改为右下角 Toast 弹出通知");
            sb.AppendLine("    右键游戏卡片菜单新增「删除游戏」选项");
            sb.AppendLine("    删除游戏时同步清理 GMD 文件和图片目录");
            sb.AppendLine("    Toast 通知支持动画滑入/滑出，新通知自动替换旧通知");
            sb.AppendLine("    详情页启动游戏后自动缩至托盘");
            sb.AppendLine();
            sb.AppendLine("  Bug 修复");
            sb.AppendLine("    修复自动扫描发现新游戏后弹出阻塞对话框");
            sb.AppendLine("    修复删除游戏时 ConsistencyCheckLog 外键约束失败");
            sb.AppendLine("    修复分页翻页因 _currentPage 无条件重置导致失效");
            sb.AppendLine("    修复 .lnk 快捷方式启动后进程追踪不匹配");
            sb.AppendLine();
            sb.AppendLine("v3.2 (2026-05-22)");
            sb.AppendLine(sep);
            sb.AppendLine("  Bug 修复");
            sb.AppendLine("    修复图片加载同步阻塞导致的死锁风险");
            sb.AppendLine("    修复磁盘扫描服务黑名单目录遍历效率问题");
            sb.AppendLine("    修复自动扫描循环内重复创建服务实例");
            sb.AppendLine("    修复收藏夹管理 N+1 数据库查询问题");
            sb.AppendLine("    修复窗口激活竞态条件导致并发数据库冲突");
            sb.AppendLine();
            sb.AppendLine("  性能优化");
            sb.AppendLine("    消除图片转换代码重复，提取共享工具方法");
            sb.AppendLine("    统一时间格式化逻辑，移除三处重复实现");
            sb.AppendLine("    优化游戏卡片统计刷新，按需更新减少 UI 遍历");
            sb.AppendLine("    数据同步签名计算改为轻量级 COUNT+MAX 检查");
            sb.AppendLine("    修复 GMD 文件锁字典内存泄漏隐患");
            sb.AppendLine();
            sb.AppendLine("  架构重构");
            sb.AppendLine("    重构 UpdateChecker 静态状态管理为实例模式");
            sb.AppendLine("    引入依赖注入容器 (Microsoft.Extensions.DI)");
            sb.AppendLine("    MainWindow 代码通过 partial class 拆分");
            sb.AppendLine("    提取 GameImageLoader 独立服务类");
            sb.AppendLine("    删除冗余 DatabaseInitializer 类");
            sb.AppendLine();
            sb.AppendLine("v3.1 (2026-05-18)");
            sb.AppendLine(sep);
            sb.AppendLine("  游戏唯一标识符系统 (GID)");
            sb.AppendLine("    为每个游戏分配独立唯一ID");
            sb.AppendLine("    采用 GID+9位数字 固定格式");
            sb.AppendLine("    确保跨数据库的唯一识别与关联");
            sb.AppendLine();
            sb.AppendLine("  GMD 文件管理优化");
            sb.AppendLine("    统一使用游戏GID作为GMD文件名");
            sb.AppendLine("    设置页面新增清理旧GMD文件按钮");
            sb.AppendLine();
            sb.AppendLine("  GMD 文件选择流程优化");
            sb.AppendLine("    选择GMD文件后自动完成添加");
            sb.AppendLine("    去除二次确认步骤，操作更流畅");
            sb.AppendLine();
            sb.AppendLine("  资源文件管理改进");
            sb.AppendLine("    GMD导入时自动创建专用资源文件夹");
            sb.AppendLine("    图标/预览图统一存储，移除临时目录依赖");
            sb.AppendLine();
            sb.AppendLine("  功能权限控制调整");
            sb.AppendLine("    GMD导入功能仅在添加游戏页面可用");
            sb.AppendLine("    编辑游戏页面移除GMD导入入口");
            sb.AppendLine();
            sb.AppendLine("v3.0 (2026-05-13)");
            sb.AppendLine(sep);
            sb.AppendLine("  全新云雾磨砂玻璃 UI 设计语言");
            sb.AppendLine("    窗口背景升级为磨砂玻璃效果");
            sb.AppendLine("    游戏卡片采用半透明磨砂质感");
            sb.AppendLine("    云雾渐变装饰层营造氛围感");
            sb.AppendLine("    弹窗全面升级磨砂玻璃风格");
            sb.AppendLine("    标签采用半透明磨砂胶囊设计");
            sb.AppendLine("    按钮升级磨砂质感交互");
            sb.AppendLine("    深色/浅色模式完美适配");
            sb.AppendLine();
            sb.AppendLine("  GitHub 更新检查");
            sb.AppendLine("    启动时自动检查 GitHub 最新版本");
            sb.AppendLine("    检测到新版本时弹出更新提示对话框");
            sb.AppendLine("    版本日志窗口增加手动检查更新按钮");
            sb.AppendLine("    支持 API/Atom Feed/HTML 三级回退检测");
            sb.AppendLine();
            sb.AppendLine("  游戏收藏与检索功能");
            sb.AppendLine("    支持收藏游戏并快速访问收藏列表");
            sb.AppendLine("    增强游戏搜索与标签筛选能力");
            sb.AppendLine("    标签系统交互体验优化");
            sb.AppendLine();
            sb.AppendLine("v2.1.1 (2026-02-24)");
            sb.AppendLine(sep);
            sb.AppendLine("  黑暗模式弹窗适配");
            sb.AppendLine("    添加/编辑游戏弹窗完美适配黑暗模式");
            sb.AppendLine("    背景、文本自动跟随系统主题");
            sb.AppendLine();
            sb.AppendLine("  标签下拉菜单优化");
            sb.AppendLine("    新增独立下拉按钮选择已有标签");
            sb.AppendLine("    避免输入时自动弹出建议干扰");
            sb.AppendLine();
            sb.AppendLine("  游戏卡片高亮修复");
            sb.AppendLine("    修复黑暗模式下鼠标离开卡片后高亮残留");
            sb.AppendLine("    悬停状态切换正常");
            sb.AppendLine();
            sb.AppendLine("  弹窗滚动功能");
            sb.AppendLine("    所有弹窗支持垂直滚动");
            sb.AppendLine("    内容再多也不会溢出重叠");
            sb.AppendLine();
            sb.AppendLine("v2.1 (2026-02-23)");
            sb.AppendLine(sep);
            sb.AppendLine("  游戏详情页编辑功能");
            sb.AppendLine("    在游戏详情页面新增编辑按钮");
            sb.AppendLine("    可直接修改游戏信息");
            sb.AppendLine();
            sb.AppendLine("  编辑按钮布局优化");
            sb.AppendLine("    编辑按钮与启动游戏按钮并排显示");
            sb.AppendLine();
            sb.AppendLine("  编辑后数据实时更新");
            sb.AppendLine("    保存后自动写入数据库");
            sb.AppendLine();
            sb.AppendLine("v2.0 (2026-02-21) - 正式版大更新");
            sb.AppendLine(sep);
            sb.AppendLine("  全新 UI/UX 设计");
            sb.AppendLine("  深色/浅色主题切换");
            sb.AppendLine("  游戏排序功能（5种方式）");
            sb.AppendLine("  收藏夹功能（数据库支持）");
            sb.AppendLine("  优化游戏卡片尺寸");
            sb.AppendLine();
            sb.AppendLine("v1.8.1 (2026-02-20)");
            sb.AppendLine(sep);
            sb.AppendLine("  修复标签输入框第一次点击不显示下拉框");
            sb.AppendLine();
            sb.AppendLine("v1.8 (2026-02-20)");
            sb.AppendLine(sep);
            sb.AppendLine("  新增标签系统");
            sb.AppendLine("  新增标签筛选功能");
            sb.AppendLine("  增强搜索功能（支持标签搜索）");
            sb.AppendLine();
            sb.AppendLine("v1.7 (2026-02-15)");
            sb.AppendLine(sep);
            sb.AppendLine("  游戏启动时自动最小化到托盘");
            sb.AppendLine("  游戏结束时自动恢复窗口");
            sb.AppendLine();
            sb.AppendLine("v1.6 (2026-02-09)");
            sb.AppendLine(sep);
            sb.AppendLine("  新增游戏详情页面");
            sb.AppendLine("  新增游戏预览图功能");
            sb.AppendLine("  新增预览图点击放大查看");
            sb.AppendLine();
            sb.AppendLine("v1.0 (2026-02-07)");
            sb.AppendLine(sep);
            sb.AppendLine("  首个正式版本发布");

            var changelogText = sb.ToString();

            var contentGrid = new Grid();

            var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            titleRow.Children.Add(new TextBlock
            {
                Text = "GameLauncher 更新日志",
                Style = (Style)App.Current.Resources["TitleTextBlockStyle"]
            });
            var checkUpdateButton = new Button
            {
                Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { new FontIcon { Glyph = "\uE895", FontSize = 14 }, new TextBlock { Text = "检查更新", VerticalAlignment = VerticalAlignment.Center } } },
                Style = (Style)App.Current.Resources["FrostedAccentButtonStyle"],
                Padding = new Microsoft.UI.Xaml.Thickness(12, 6, 12, 6),
                Margin = new Microsoft.UI.Xaml.Thickness(0, 0, 0, 12)
            };
            titleRow.Children.Add(checkUpdateButton);

            contentGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            contentGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            Grid.SetRow(titleRow, 0);
            contentGrid.Children.Add(titleRow);

            var scrollViewer = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollMode = ScrollMode.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MaxHeight = 480,
                Content = new TextBlock
                {
                    Text = changelogText,
                    FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Microsoft YaHei UI"),
                    FontSize = 13,
                    TextWrapping = TextWrapping.Wrap
                }
            };
            Grid.SetRow(scrollViewer, 1);
            contentGrid.Children.Add(scrollViewer);

            var dialog = new ContentDialog
            {
                Title = "更新日志",
                Content = contentGrid,
                CloseButtonText = "关闭",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Content.XamlRoot,
                Style = (Style)App.Current.Resources["DefaultContentDialogStyle"]
            };

            checkUpdateButton.Click += async (s, e) =>
            {
                dialog.Hide();
                await Task.Delay(200);
                CheckForUpdatesManually();
            };

            await dialog.ShowAsync();
        }

        private async void ShowUpdateAvailableDialog(UpdateInfo updateInfo)
        {
            if (_isShowingUpdateDialog) return;
            _isShowingUpdateDialog = true;

            try
            {
                var accentColor = (Windows.UI.Color)App.Current.Resources["SystemAccentColor"];
                var accentBrush = new SolidColorBrush(accentColor);
                var secondaryBrush = (Microsoft.UI.Xaml.Media.Brush)App.Current.Resources["TextFillColorSecondaryBrush"];

                var contentGrid = new Grid();
                contentGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                contentGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

                var infoStack = new StackPanel { Spacing = 8 };
                infoStack.Children.Add(new TextBlock
                {
                    Text = $"发现新版本 v{updateInfo.LatestVersion}",
                    Style = (Style)App.Current.Resources["SubtitleTextBlockStyle"],
                    Foreground = accentBrush
                });
                infoStack.Children.Add(new TextBlock
                {
                    Text = $"当前版本: v{updateInfo.CurrentVersion}",
                    Style = (Style)App.Current.Resources["BodyTextBlockStyle"],
                    Foreground = secondaryBrush
                });
                if (updateInfo.PublishedAt.HasValue)
                {
                    infoStack.Children.Add(new TextBlock
                    {
                        Text = $"发布日期: {updateInfo.PublishedAt.Value:yyyy-MM-dd}",
                        Style = (Style)App.Current.Resources["BodyTextBlockStyle"],
                        Foreground = secondaryBrush
                    });
                }
                Grid.SetRow(infoStack, 0);
                contentGrid.Children.Add(infoStack);

                var scrollViewer = new ScrollViewer
                {
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    VerticalScrollMode = ScrollMode.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    MaxHeight = 400,
                    Margin = new Microsoft.UI.Xaml.Thickness(0, 12, 0, 0),
                    Content = new TextBlock
                    {
                        Text = updateInfo.ReleaseNotes,
                        FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Microsoft YaHei UI"),
                        FontSize = 13,
                        TextWrapping = TextWrapping.Wrap
                    }
                };
                Grid.SetRow(scrollViewer, 1);
                contentGrid.Children.Add(scrollViewer);

                var dialog = new ContentDialog
                {
                    Title = "发现新版本",
                    Content = contentGrid,
                    PrimaryButtonText = "前往下载",
                    CloseButtonText = "稍后再说",
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = Content.XamlRoot,
                    Style = (Style)App.Current.Resources["DefaultContentDialogStyle"]
                };

                var result = await dialog.ShowAsync();
                if (result == ContentDialogResult.Primary)
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = updateInfo.DownloadUrl,
                        UseShellExecute = true
                    });
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"显示更新对话框失败: {ex.Message}");
            }
            finally
            {
                _isShowingUpdateDialog = false;
            }
        }

        private async void CheckForUpdatesManually()
        {
            if (_isDialogOpen) return;

            try
            {
                _isDialogOpen = true;

                var updateInfo = await _updateChecker.CheckForUpdateAsync(forceCheck: true);

                if (updateInfo != null)
                {
                    ShowUpdateAvailableDialog(updateInfo);
                }
                else
                {
                    var accentColor = (Windows.UI.Color)App.Current.Resources["SystemAccentColor"];
                    var accentBrush = new SolidColorBrush(accentColor);
                    var currentVersion = _updateChecker.CurrentVersion;

                    var upToDateDialog = new ContentDialog
                    {
                        Title = "检查更新",
                        Content = new StackPanel
                        {
                            Spacing = 12,
                            Children =
                            {
                                new FontIcon { Glyph = "\uE8FB", FontSize = 48, Foreground = accentBrush },
                                new TextBlock
                                {
                                    Text = "当前已是最新版本",
                                    Style = (Style)App.Current.Resources["BodyStrongTextBlockStyle"],
                                    HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Center
                                },
                                new TextBlock
                                {
                                    Text = $"当前版本: v{currentVersion}",
                                    Style = (Style)App.Current.Resources["BodyTextBlockStyle"],
                                    Foreground = (Microsoft.UI.Xaml.Media.Brush)App.Current.Resources["TextFillColorSecondaryBrush"],
                                    HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Center
                                }
                            }
                        },
                        CloseButtonText = "确定",
                        XamlRoot = Content.XamlRoot,
                        Style = (Style)App.Current.Resources["DefaultContentDialogStyle"]
                    };
                    await upToDateDialog.ShowAsync();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"手动检查更新失败: {ex.Message}");
                ShowToast("检查更新失败", "无法连接到GitHub服务器，请检查网络连接。", ToastType.Error);
            }
            finally
            {
                _isDialogOpen = false;
            }
        }

        private async void ScanGamesButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isDialogOpen) return;
            try
            {
                _isDialogOpen = true;
                var dialog = new Views.DiskScanDialog(_gameService, _gameImageLoader, _games)
                {
                    XamlRoot = Content.XamlRoot
                };
                var result = await dialog.ShowAsync();

                if (dialog.SelectedGames.Count > 0)
                {
                    int imported = await _gameService.ImportGamesAsync(dialog.SelectedGames);
                    if (imported > 0)
                    {
                        await SilentRefreshGamesAsync(forceUiUpdate: true);
                    }
                }

                if (dialog.AllDiscoveredGames != null)
                {
                    var selectedIds = new HashSet<string>(
                        dialog.SelectedGames.Select(g => g.GameId),
                        StringComparer.OrdinalIgnoreCase);
                    var notSelected = dialog.AllDiscoveredGames
                        .Where(g => !string.IsNullOrWhiteSpace(g.GameId) && !selectedIds.Contains(g.GameId))
                        .ToList();
                    foreach (var game in notSelected)
                    {
                        try
                        {
                            var imageService = _imageService;
                            imageService.DeleteGameImages(game.GameId);
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"扫描游戏时出错: {ex.Message}");
            }
            finally
            {
                _isDialogOpen = false;
            }
        }

        private async void ManageCollectionsButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isDialogOpen) return;
            try
            {
                _isDialogOpen = true;
                var dialog = new Views.CollectionManageDialog(_gameService)
                {
                    XamlRoot = Content.XamlRoot
                };
                await dialog.ShowAsync();
                await RefreshCollectionFilterAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"管理收藏夹时出错: {ex.Message}");
            }
            finally
            {
                _isDialogOpen = false;
            }
        }

        private async Task ShowGameContextMenu(Game game, FrameworkElement target)
        {
            var menuFlyout = new MenuFlyout();

            var addToCollectionItem = new MenuFlyoutSubItem { Text = "添加到收藏夹" };

            try
            {
                var collections = await _gameService.GetAllCollectionsAsync();
                var gameCols = await _gameService.GetCollectionsForGameAsync(game.Id);
                var gameColIds = gameCols.Select(c => c.Id).ToHashSet();

                foreach (var col in collections)
                {
                    var item = new ToggleMenuFlyoutItem
                    {
                        Text = col.Name,
                        IsChecked = gameColIds.Contains(col.Id)
                    };
                    var colId = col.Id;
                    var colRef = col;
                    item.Click += async (s, e) =>
                    {
                        if (item.IsChecked)
                        {
                            await _gameService.AddGameToCollectionAsync(game.Id, colId);
                            if (!game.Collections.Any(c => c.Id == colId))
                                game.Collections.Add(colRef);
                        }
                        else
                        {
                            await _gameService.RemoveGameFromCollectionAsync(game.Id, colId);
                            var toRemove = game.Collections.FirstOrDefault(c => c.Id == colId);
                            if (toRemove != null)
                                game.Collections.Remove(toRemove);
                        }

                        RunOnUi(() => ApplyFilters());
                        RunOnUi(async () => await RefreshCollectionFilterAsync());
                    };
                    addToCollectionItem.Items.Add(item);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"加载收藏夹菜单失败: {ex.Message}");
                var errorItem = new MenuFlyoutItem { Text = "加载失败" };
                addToCollectionItem.Items.Add(errorItem);
            }

            menuFlyout.Items.Add(addToCollectionItem);

            menuFlyout.Items.Add(new MenuFlyoutSeparator());

            var deleteItem = new MenuFlyoutItem { Text = "删除游戏" };
            deleteItem.Click += async (s, e) =>
            {
                var (confirmed, deleteGmd) = await ShowDeleteConfirmDialog(game.Name);
                if (confirmed)
                {
                    var success = await _gameService.DeleteGameAsync(game.Id, deleteGmd);
                    RunOnUi(async () =>
                    {
                        if (success)
                        {
                            ShowToast("删除成功", $"游戏「{game.Name}」已删除", ToastType.Success);
                            await SilentRefreshGamesAsync(forceUiUpdate: true);
                        }
                        else
                        {
                            ShowToast("删除失败", "删除游戏时发生错误", ToastType.Error);
                        }
                    });
                }
            };
            menuFlyout.Items.Add(deleteItem);

            menuFlyout.ShowAt(target);
        }

        private DataTemplate CreateGameListItemTemplate()
        {
            var xaml = @"
<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>
    <StackPanel Orientation='Horizontal' Spacing='12'>
        <Image Source='{Binding IconSource}' Width='32' Height='32' Stretch='Uniform'/>
        <StackPanel>
            <TextBlock Text='{Binding Name}' FontWeight='SemiBold'/>
            <TextBlock Text='{Binding ExecutablePath}'
                       FontSize='12'
                       Foreground='Gray'
                       TextTrimming='CharacterEllipsis'
                       MaxWidth='350'/>
        </StackPanel>
    </StackPanel>
</DataTemplate>";
            return (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(xaml);
        }

        private void MainWindow_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            var ctrlState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control);
            bool isCtrlPressed = ctrlState.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

            if (isCtrlPressed && e.Key == Windows.System.VirtualKey.K)
            {
                e.Handled = true;
                if (SearchBox != null)
                {
                    SearchBox.Focus(FocusState.Programmatic);
                    var textBox = FindVisualChild<Microsoft.UI.Xaml.Controls.TextBox>(SearchBox);
                    if (textBox != null)
                    {
                        textBox.SelectAll();
                    }
                }
            }
        }

        private void PrevPageButton_Click(object sender, RoutedEventArgs e)
        {
            GoToPrevPage();
        }

        private void NextPageButton_Click(object sender, RoutedEventArgs e)
        {
            GoToNextPage();
        }

        private void PageSizeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (PageSizeComboBox?.SelectedItem is ComboBoxItem item && item.Tag is string tag)
            {
                if (int.TryParse(tag, out int size))
                {
                    PageSizeChanged(size);
                }
            }
        }
    }
}