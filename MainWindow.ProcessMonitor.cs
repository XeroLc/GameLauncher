using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GameLauncher.Models;
using GameLauncher.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GameLauncher
{
    public sealed partial class MainWindow : Window
    {
        private volatile bool _isClosing = false;
        private DispatcherTimer _statusCheckTimer;
        private readonly ConcurrentDictionary<int, DateTime> _runningGames = new();
        private readonly HashSet<int> _lastRunningGameIds = new();

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

        private void CheckRunningGames()
        {
            if (_games == null || _gameService == null || _isClosing)
            {
                return;
            }

            bool hadRunningGames = _runningGames.Count > 0;

            foreach (var game in _games.Where(g => g.IsRunning))
            {
                if (!_runningGames.ContainsKey(game.Id))
                {
                    _runningGames[game.Id] = DateTime.UtcNow;
                }
            }

            HashSet<string> runningProcessNames;
            try
            {
                runningProcessNames = new HashSet<string>(
                    Process.GetProcesses().Select(p => { try { var name = p.ProcessName; p.Dispose(); return name; } catch { return ""; } }).Where(n => !string.IsNullOrEmpty(n)),
                    StringComparer.OrdinalIgnoreCase);
            }
            catch { return; }

            foreach (var kvp in _runningGames.ToList())
            {
                var gameId = kvp.Key;
                var game = _games.FirstOrDefault(g => g.Id == gameId);
                if (game == null)
                {
                    _runningGames.TryRemove(gameId, out _);
                    continue;
                }

                var processName = Path.GetFileNameWithoutExtension(game.ExecutablePath);
                bool isRunning = runningProcessNames.Contains(processName);

                if (!isRunning)
                {
                    var runTime = (long)(DateTime.UtcNow - kvp.Value).TotalSeconds;
                    
                    _ = _gameService.UpdateGamePlayTimeAsync(gameId, runTime);
                    
                    _runningGames.TryRemove(gameId, out _);
                    
                    if (!_isClosing)
                    {
                        RunOnUi(() =>
                        {
                            try
                            {
                                game.IsRunning = false;
                                game.TotalPlayTime += runTime;
                                UpdateGameCardStatistics();
                            }
                            catch
                            {
                            }
                        });
                    }
                }
            }

            if (hadRunningGames && _runningGames.Count == 0)
            {
                RunOnUi(() => _trayService.RestoreFromTray());
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
                    RunOnUi(() => {
                        game.IsRunning = true;
                    });
                    await Task.Delay(100);
                    UpdateGameCardStatistics();
                    _trayService.MinimizeToTray();
                }
                else
                {
                    RunOnUi(() => ShowToast("启动失败", "无法启动游戏，请检查游戏路径是否正确", ToastType.Error));
                }
            }
        }
    }
}