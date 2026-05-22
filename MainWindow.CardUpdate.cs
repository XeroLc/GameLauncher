using System;
using System.Collections.Generic;
using System.Linq;
using GameLauncher.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GameLauncher
{
    public sealed partial class MainWindow : Window
    {
        private readonly Dictionary<int, (int launchCount, long totalPlayTime)> _lastGameStats = new();

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