using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GameLauncher.Models;
using GameLauncher.Services;
using Microsoft.Extensions.DependencyInjection;
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
            UpdateTransferProgress();
            UpdateGameCardAvailability();
        }

        private void CheckRunningGames()
        {
            if (_games == null || _gameService == null || _isClosing)
            {
                return;
            }

            // 收养：把内存中标记为运行但尚未监控的游戏加入监控
            // （数据库 IsRunning=1 的唯一补救途径，必须在早退判断之前执行）
            foreach (var game in _games.Where(g => g.IsRunning))
            {
                if (!_runningGames.ContainsKey(game.Id))
                {
                    _runningGames[game.Id] = DateTime.UtcNow;
                }
            }

            // 无运行中的游戏时跳过进程枚举与结算，避免每 5 秒全量枚举系统进程
            if (_runningGames.Count == 0)
            {
                return;
            }

            bool hadRunningGames = _runningGames.Count > 0;

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
                // 本地文件不存在但有云备份 → 走下载恢复流程
                if (!game.IsInstalled && game.HasCloudBackup)
                {
                    await DownloadGameFromCloudAsync(game);
                    return;
                }

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

        private async Task DownloadGameFromCloudAsync(Game game)
        {
            var archiveService = App.Services.GetRequiredService<GameArchiveService>();
            var progress = new Progress<ArchiveProgress>(p =>
            {
                // Done 阶段不写入（避免在 TryRemove 之后重新写入导致进度条卡住）
                if (p.Stage == ArchiveProgress.TransferStage.Done)
                    return;
                _transferTasks[game.Id] = p;
                UpdateTransferProgress();
            });
            _transferTasks[game.Id] = new ArchiveProgress { Stage = ArchiveProgress.TransferStage.Downloading, OverallPercent = 0 };
            UpdateGameCardAvailability();
            try
            {
                await archiveService.DownloadGameAsync(game, progress);
                _transferTasks.TryRemove(game.Id, out _);
                RunOnUi(async () =>
                {
                    ShowToast("恢复完成", $"{game.Name} 已恢复到本地", ToastType.Success);
                    await SilentRefreshGamesAsync(forceUiUpdate: true);
                    UpdateGameCardAvailability();
                });
            }
            catch (Exception ex)
            {
                _transferTasks.TryRemove(game.Id, out _);
                RunOnUi(() => ShowToast("下载失败", ex.Message, ToastType.Error));
            }
            finally
            {
                RunOnUi(UpdateGameCardAvailability);
            }
        }
    }
}