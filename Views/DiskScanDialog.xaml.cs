using GameLauncher.Models;
using GameLauncher.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace GameLauncher.Views
{
    public sealed partial class DiskScanDialog : ContentDialog
    {
        private readonly GameService _gameService;
        private readonly ObservableCollection<Game> _existingGames;
        private CancellationTokenSource? _cts;
        private ScanResult? _scanResult;
        private readonly ObservableCollection<Game> _displayedGames = new();

        public List<Game> SelectedGames { get; private set; } = new();
        public List<Game> AllDiscoveredGames { get; private set; } = new();

        public DiskScanDialog(GameService gameService, ObservableCollection<Game> existingGames)
        {
            _gameService = gameService;
            _existingGames = existingGames;
            this.InitializeComponent();

            ResultsListView.ItemsSource = _displayedGames;
            IsPrimaryButtonEnabled = false;

            PrimaryButtonClick += DiskScanDialog_PrimaryButtonClick;

            _ = StartScanAsync();
        }

        private void DiskScanDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            SelectedGames = ResultsListView.SelectedItems.Cast<Game>().ToList();
        }

        private async Task StartScanAsync()
        {
            _cts = new CancellationTokenSource();

            var progress = new Progress<ScanProgress>(p =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    ScanStatusText.Text = $"正在扫描: {p.CurrentDrive}";
                    ScanDetailText.Text = p.Message;
                    if (!double.IsNaN(p.Percentage) && p.Percentage > 0)
                    {
                        ScanProgressBar.IsIndeterminate = false;
                        ScanProgressBar.Value = p.Percentage;
                    }
                    FoundCountText.Text = p.FoundCount.ToString();
                    SkippedCountText.Text = p.SkippedCount.ToString();
                    ErrorCountText.Text = p.ErrorCount.ToString();
                });
            });

            try
            {
                _scanResult = await _gameService.ScanForGmdFilesAsync(
                    _existingGames, progress, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                ScanStatusText.Text = "扫描已取消";
                return;
            }
            catch (Exception ex)
            {
                ScanStatusText.Text = $"扫描出错: {ex.Message}";
                return;
            }

            ShowResults();
        }

        private void ShowResults()
        {
            if (_scanResult == null) return;

            ScanProgressPanel.Visibility = Visibility.Collapsed;
            ResultsPanel.Visibility = Visibility.Visible;

            int newCount = _scanResult.DiscoveredGames.Count;
            int existCount = _scanResult.ExistingGames.Count;
            int failCount = _scanResult.FailedFiles.Count;

            ResultsTitleText.Text = $"扫描完成 - 发现 {newCount} 个新游戏";
            ResultsSummaryText.Text = $"新发现: {newCount} | 已存在: {existCount} | 失败: {failCount}";

            _displayedGames.Clear();
            foreach (var game in _scanResult.DiscoveredGames)
            {
                try
                {
                    game.LoadIcon();
                }
                catch { }
                _displayedGames.Add(game);
            }

            AllDiscoveredGames = _scanResult.DiscoveredGames.ToList();

            if (newCount == 0)
            {
                IsPrimaryButtonEnabled = false;
                PrimaryButtonText = "关闭";
            }
            else
            {
                IsPrimaryButtonEnabled = true;
                foreach (var game in _displayedGames)
                {
                    ResultsListView.SelectedItems.Add(game);
                }
            }

            CancelScanButton.Visibility = Visibility.Collapsed;
        }

        private void CancelScanButton_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
        }

        private void SelectAllCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (ResultsListView == null) return;
            if (SelectAllCheckBox.IsChecked == true)
            {
                try { ResultsListView.SelectAll(); } catch { }
            }
            else
            {
                try { ResultsListView.SelectedItems.Clear(); } catch { }
            }
        }
    }
}