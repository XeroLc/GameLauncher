using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using GameLauncher.Models;
using GameLauncher.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace GameLauncher
{
    public sealed partial class MainWindow : Window
    {
        private readonly Dictionary<int, (int launchCount, long totalPlayTime)> _lastGameStats = new();
        /// <summary>进行中的归档/下载任务（游戏Id → 进度）</summary>
        private readonly ConcurrentDictionary<int, ArchiveProgress> _transferTasks = new();

        private void UpdateTransferProgress()
        {
            RunOnUi(() =>
            {
                try
                {
                    if (GamesGridView == null || _games == null)
                        return;

                    foreach (var game in _games)
                    {
                        try
                        {
                            var container = GamesGridView.ContainerFromItem(game) as GridViewItem;
                            if (container == null) continue;
                            var root = container.ContentTemplateRoot as FrameworkElement;
                            if (root == null) continue;

                            var transferPanel = root.FindName("TransferProgressPanel") as Grid;
                            var fillScale = root.FindName("TransferFillScale") as ScaleTransform;
                            var transferText = root.FindName("TransferProgressText") as TextBlock;
                            var launchBtn = root.FindName("LaunchButton") as Button;
                            var stopBtn = root.FindName("StopButton") as Button;

                            if (transferPanel == null || launchBtn == null) continue;

                            if (_transferTasks.TryGetValue(game.Id, out var progress))
                            {
                                transferPanel.Visibility = Visibility.Visible;
                                launchBtn.Visibility = Visibility.Collapsed;
                                if (stopBtn != null) stopBtn.Visibility = Visibility.Collapsed;
                                if (fillScale != null) fillScale.ScaleX = Math.Clamp(progress.OverallPercent / 100.0, 0, 1);
                                if (transferText != null)
                                {
                                    var label = progress.Stage switch
                                    {
                                        ArchiveProgress.TransferStage.Packaging => "打包中",
                                        ArchiveProgress.TransferStage.Uploading => "上传中",
                                        ArchiveProgress.TransferStage.Downloading => "下载中",
                                        _ => ""
                                    };
                                    transferText.Text = $"{label} {(int)progress.OverallPercent}%";
                                }
                            }
                            else
                            {
                                transferPanel.Visibility = Visibility.Collapsed;
                            }
                        }
                        catch
                        {
                        }
                    }
                }
                catch
                {
                }
            });
        }

        /// <summary>刷新卡片启动/下载/终止按钮状态（含传输中状态）</summary>
        private void UpdateGameCardAvailability()
        {
            RunOnUi(() =>
            {
                try
                {
                    if (GamesGridView == null || _games == null)
                        return;

                    foreach (var game in _games)
                    {
                        try
                        {
                            var container = GamesGridView.ContainerFromItem(game) as GridViewItem;
                            if (container == null) continue;
                            var root = container.ContentTemplateRoot as FrameworkElement;
                            if (root == null) continue;

                            var transferPanel = root.FindName("TransferProgressPanel") as Grid;
                            var launchBtn = root.FindName("LaunchButton") as Button;
                            var stopBtn = root.FindName("StopButton") as Button;
                            var launchText = root.FindName("LaunchButtonText") as TextBlock;
                            var launchIcon = root.FindName("LaunchButtonIcon") as FontIcon;
                            if (launchBtn == null) continue;

                            // 传输中：隐藏按钮显示进度条
                            if (_transferTasks.ContainsKey(game.Id))
                            {
                                if (transferPanel != null) transferPanel.Visibility = Visibility.Visible;
                                launchBtn.Visibility = Visibility.Collapsed;
                                if (stopBtn != null) stopBtn.Visibility = Visibility.Collapsed;
                                continue;
                            }

                            if (transferPanel != null) transferPanel.Visibility = Visibility.Collapsed;

                            if (game.IsRunning)
                            {
                                launchBtn.Visibility = Visibility.Collapsed;
                                if (stopBtn != null) stopBtn.Visibility = Visibility.Visible;
                                continue;
                            }

                            if (stopBtn != null) stopBtn.Visibility = Visibility.Collapsed;
                            launchBtn.Visibility = Visibility.Visible;
                            if (!game.IsInstalled && game.HasCloudBackup)
                            {
                                if (launchText != null) launchText.Text = "下载游戏";
                                if (launchIcon != null) launchIcon.Glyph = "";
                            }
                            else
                            {
                                if (launchText != null) launchText.Text = "启动游戏";
                                if (launchIcon != null) launchIcon.Glyph = "";
                            }
                        }
                        catch
                        {
                        }
                    }
                }
                catch
                {
                }
            });
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
            RunOnUi(() =>
            {
                try
                {
                    if (GamesGridView == null || _games == null)
                    {
                        return;
                    }

                    var currentRunningIds = new HashSet<int>(_games.Where(g => g.IsRunning).Select(g => g.Id));
                    bool runningStateChanged = !currentRunningIds.SetEquals(_lastRunningGameIds);

                    if (runningStateChanged)
                    {
                        _lastRunningGameIds.Clear();
                        foreach (var id in currentRunningIds)
                            _lastRunningGameIds.Add(id);
                    }

                    foreach (var game in _games)
                    {
                        try
                        {
                            bool hasPrevStats = _lastGameStats.TryGetValue(game.Id, out var prevStats);
                            bool launchCountChanged = !hasPrevStats || prevStats.launchCount != game.LaunchCount;
                            bool playTimeChanged = !hasPrevStats || prevStats.totalPlayTime != game.TotalPlayTime;
                            bool statsChanged = launchCountChanged || playTimeChanged;

                            if (!statsChanged && !runningStateChanged)
                            {
                                continue;
                            }

                            var container = GamesGridView.ContainerFromItem(game) as GridViewItem;
                            if (container == null) continue;

                            var root = container.ContentTemplateRoot as FrameworkElement;
                            if (root == null) continue;

                            if (runningStateChanged)
                            {
                                var runningIndicatorGrid = root.FindName("RunningIndicatorGrid") as Grid;
                                if (runningIndicatorGrid != null)
                                {
                                    runningIndicatorGrid.Visibility = game.IsRunning ? Visibility.Visible : Visibility.Collapsed;
                                }

                                var launchBtn = root.FindName("LaunchButton") as Button;
                                var stopBtn = root.FindName("StopButton") as Button;
                                if (launchBtn != null && stopBtn != null)
                                {
                                    launchBtn.Visibility = game.IsRunning ? Visibility.Collapsed : Visibility.Visible;
                                    stopBtn.Visibility = game.IsRunning ? Visibility.Visible : Visibility.Collapsed;
                                }
                            }

                            if (launchCountChanged)
                            {
                                var launchCountText = root.FindName("LaunchCountText") as TextBlock;
                                if (launchCountText != null)
                                {
                                    launchCountText.Text = $"{game.LaunchCount}次";
                                }
                            }

                            if (playTimeChanged)
                            {
                                var playTimeText = root.FindName("PlayTimeText") as TextBlock;
                                if (playTimeText != null)
                                {
                                    playTimeText.Text = Game.FormatPlayTime(game.TotalPlayTime);
                                }
                            }

                            _lastGameStats[game.Id] = (game.LaunchCount, game.TotalPlayTime);
                        }
                        catch
                        {
                        }
                    }

                    var currentGameIds = new HashSet<int>(_games.Select(g => g.Id));
                    var staleIds = _lastGameStats.Keys.Where(k => !currentGameIds.Contains(k)).ToList();
                    foreach (var staleId in staleIds)
                    {
                        _lastGameStats.Remove(staleId);
                    }
                }
                catch
                {
                }
            });
        }
    }
}