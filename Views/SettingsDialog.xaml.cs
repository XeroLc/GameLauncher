using GameLauncher.Data;
using GameLauncher.Models;
using GameLauncher.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace GameLauncher.Views
{
    public sealed partial class SettingsDialog : ContentDialog
    {
        private ObservableCollection<string> _scanPaths;
        private bool _isLoaded;
        private bool _syncUiLoaded;
        private readonly IncrementalSyncService _syncService;

        public SettingsDialog()
        {
            this.InitializeComponent();
            _syncService = App.Services.GetRequiredService<IncrementalSyncService>();
            _syncService.ProgressChanged += OnSyncProgressChanged;
            _syncService.SyncCompleted += OnSyncCompleted;
            Closed += OnSettingsDialogClosed;
            LoadSettings();
            LoadSyncSettings();
        }

        private void LoadSettings()
        {
            var settings = UserSettings.Instance;
            _scanPaths = new ObservableCollection<string>(settings.ScanPaths ?? new System.Collections.Generic.List<string>());
            ScanPathListView.ItemsSource = _scanPaths;
            _isLoaded = true;

            HideUnavailableGamesToggle.IsOn = settings.HideUnavailableGames;
            AutoScanToggle.IsOn = settings.AutoScanEnabled;
            DebugModeToggle.IsOn = settings.DebugModeEnabled;

            UpdateScanPanelVisibility();

            // 根据私密模式状态控制私密设置面板的显示
            UpdatePrivateModePanelVisibility();
        }

        private void UpdateScanPanelVisibility()
        {
            ScanPathsPanel.Visibility = AutoScanToggle.IsOn
                ? Microsoft.UI.Xaml.Visibility.Visible
                : Microsoft.UI.Xaml.Visibility.Collapsed;
        }

        private void HideUnavailableGamesToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_isLoaded) return;
            var settings = UserSettings.Instance;
            settings.HideUnavailableGames = HideUnavailableGamesToggle.IsOn;
            settings.Save();
        }

        private void AutoScanToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_isLoaded) return;
            var settings = UserSettings.Instance;
            settings.AutoScanEnabled = AutoScanToggle.IsOn;
            UpdateScanPanelVisibility();
            SaveScanPaths();
        }

        private async void AddScanPathButton_Click(object sender, RoutedEventArgs e)
        {
            var folderPicker = new FolderPicker();
            folderPicker.SuggestedStartLocation = PickerLocationId.Desktop;
            folderPicker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(folderPicker,
                WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));

            var folder = await folderPicker.PickSingleFolderAsync();
            if (folder != null && !_scanPaths.Contains(folder.Path))
            {
                _scanPaths.Add(folder.Path);
                SaveScanPaths();
            }
        }

        private void RemoveScanPathButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string path)
            {
                _scanPaths.Remove(path);
                SaveScanPaths();
            }
        }

        private void SaveScanPaths()
        {
            if (_scanPaths == null) return;
            var settings = UserSettings.Instance;
            settings.ScanPaths = _scanPaths.ToList();
            settings.Save();
        }

        private void UpdatePrivateModePanelVisibility()
        {
            // 私密模式设置面板在以下情况显示：
            // 1. 当前处于私密模式
            // 2. 尚未设置密码（需要提供设置入口）
            var isPrivate = PrivateModeService.Instance.IsPrivateMode;
            var hasPassword = PrivateModeService.Instance.HasPassword;
            PrivateModeSettingsPanel.Visibility = (isPrivate || !hasPassword)
                ? Microsoft.UI.Xaml.Visibility.Visible
                : Microsoft.UI.Xaml.Visibility.Collapsed;

            UpdatePrivatePasswordButtonText();
        }

        private void UpdatePrivatePasswordButtonText()
        {
            if (PrivateModeService.Instance.HasPassword)
            {
                PrivatePasswordButtonText.Text = "修改密码";
            }
            else
            {
                PrivatePasswordButtonText.Text = "设置密码";
            }
        }

        private void DebugModeToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_isLoaded) return;
            var settings = UserSettings.Instance;
            settings.DebugModeEnabled = DebugModeToggle.IsOn;
            settings.Save();
        }

        private void LoadSyncSettings()
        {
            var settings = UserSettings.Instance;

            CloudSyncToggle.IsOn = settings.CloudSyncEnabled;
            SyncBackendCombo.SelectedIndex =
                string.Equals(settings.CloudSyncBackend, "CloudflareR2", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            SyncFolderPathBox.Text = settings.SyncFolderPath;
            R2EndpointBox.Text = settings.R2Endpoint;
            R2BucketBox.Text = settings.R2Bucket;
            R2AccessKeyIdBox.Text = settings.R2AccessKeyId;
            if (!string.IsNullOrEmpty(settings.R2SecretAccessKey) &&
                !settings.R2SecretAccessKey.StartsWith("DPAPI:", StringComparison.Ordinal))
            {
                // 旧版明文密钥自动升级为 DPAPI 加密存储
                settings.R2SecretAccessKey = SecretProtector.Encrypt(settings.R2SecretAccessKey);
                settings.Save();
            }
            R2SecretBox.Password = SecretProtector.Decrypt(settings.R2SecretAccessKey);
            SyncDirectionCombo.SelectedIndex = settings.CloudSyncDirection switch
            {
                "UploadOnly" => 1,
                "DownloadOnly" => 2,
                _ => 0
            };
            SyncIntervalCombo.SelectedIndex = settings.CloudSyncIntervalMinutes switch
            {
                5 => 1,
                15 => 2,
                30 => 3,
                60 => 4,
                _ => 0
            };

            _syncUiLoaded = true;
            UpdateCloudSyncPanels();
            UpdateSyncStatus();
        }

        private void CloudSyncToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_syncUiLoaded) return;
            var settings = UserSettings.Instance;
            settings.CloudSyncEnabled = CloudSyncToggle.IsOn;
            settings.Save();
            UpdateCloudSyncPanels();
            _syncService.RestartTimer();
        }

        private void SyncCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_syncUiLoaded) return;
            var settings = UserSettings.Instance;

            if (ReferenceEquals(sender, SyncBackendCombo))
            {
                settings.CloudSyncBackend = SyncBackendCombo.SelectedIndex == 1 ? "CloudflareR2" : "Folder";
            }
            else if (ReferenceEquals(sender, SyncDirectionCombo))
            {
                settings.CloudSyncDirection = SyncDirectionCombo.SelectedIndex switch
                {
                    1 => "UploadOnly",
                    2 => "DownloadOnly",
                    _ => "TwoWay"
                };
            }
            else if (ReferenceEquals(sender, SyncIntervalCombo))
            {
                settings.CloudSyncIntervalMinutes = SyncIntervalCombo.SelectedIndex switch
                {
                    1 => 5,
                    2 => 15,
                    3 => 30,
                    4 => 60,
                    _ => 0
                };
            }

            settings.Save();
            UpdateCloudSyncPanels();
            _syncService.RestartTimer();
        }

        private void SyncField_LostFocus(object sender, RoutedEventArgs e)
        {
            if (!_syncUiLoaded) return;
            var settings = UserSettings.Instance;

            if (ReferenceEquals(sender, SyncFolderPathBox))
                settings.SyncFolderPath = SyncFolderPathBox.Text.Trim();
            else if (ReferenceEquals(sender, R2EndpointBox))
                settings.R2Endpoint = R2EndpointBox.Text.Trim();
            else if (ReferenceEquals(sender, R2BucketBox))
                settings.R2Bucket = R2BucketBox.Text.Trim();
            else if (ReferenceEquals(sender, R2AccessKeyIdBox))
                settings.R2AccessKeyId = R2AccessKeyIdBox.Text.Trim();
            else if (ReferenceEquals(sender, R2SecretBox))
                settings.R2SecretAccessKey = SecretProtector.Encrypt(R2SecretBox.Password);

            settings.Save();
            UpdateCloudSyncPanels();
        }

        private async void BrowseSyncFolderButton_Click(object sender, RoutedEventArgs e)
        {
            var folderPicker = new FolderPicker
            {
                SuggestedStartLocation = PickerLocationId.Desktop
            };
            folderPicker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(folderPicker,
                WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));

            var folder = await folderPicker.PickSingleFolderAsync();
            if (folder == null) return;

            SyncFolderPathBox.Text = folder.Path;
            var settings = UserSettings.Instance;
            settings.SyncFolderPath = folder.Path;
            settings.Save();
            UpdateCloudSyncPanels();
        }

        private async void SyncNowButton_Click(object sender, RoutedEventArgs e)
        {
            if (_syncService.IsSyncing) return;

            SyncNowButton.IsEnabled = false;
            SyncStatusText.Text = "正在同步...";
            try
            {
                var result = await _syncService.SyncNowAsync();
                UpdateSyncStatus();
                if (App.MainWindow is MainWindow mainWindow)
                {
                    mainWindow.ShowToast(result.Success ? "同步完成" : "同步失败", result.Summary,
                        result.Success ? ToastType.Success : ToastType.Error);
                }
            }
            catch (Exception ex)
            {
                SyncStatusText.Text = $"同步异常: {ex.Message}";
                System.Diagnostics.Debug.WriteLine($"同步异常: {ex}");
            }
            finally
            {
                UpdateCloudSyncPanels();
            }
        }

        private async void TestSyncButton_Click(object sender, RoutedEventArgs e)
        {
            TestSyncButton.IsEnabled = false;
            SyncStatusText.Text = "正在测试连接...";
            try
            {
                var error = await _syncService.TestConnectionAsync();
                SyncStatusText.Text = error == null ? "连接成功，配置可用。" : error;
                if (App.MainWindow is MainWindow mainWindow)
                {
                    mainWindow.ShowToast(error == null ? "连接成功" : "连接失败",
                        error ?? "同步后端连接正常",
                        error == null ? ToastType.Success : ToastType.Error);
                }
            }
            catch (Exception ex)
            {
                SyncStatusText.Text = $"测试异常: {ex.Message}";
                System.Diagnostics.Debug.WriteLine($"测试连接异常: {ex}");
            }
            finally
            {
                UpdateCloudSyncPanels();
            }
        }

        private void UpdateCloudSyncPanels()
        {
            CloudSyncSettingsPanel.Visibility = CloudSyncToggle.IsOn
                ? Microsoft.UI.Xaml.Visibility.Visible
                : Microsoft.UI.Xaml.Visibility.Collapsed;

            var useR2 = SyncBackendCombo.SelectedIndex == 1;
            SyncFolderPanel.Visibility = useR2
                ? Microsoft.UI.Xaml.Visibility.Collapsed
                : Microsoft.UI.Xaml.Visibility.Visible;
            R2Panel.Visibility = useR2
                ? Microsoft.UI.Xaml.Visibility.Visible
                : Microsoft.UI.Xaml.Visibility.Collapsed;

            var configured = IsSyncConfigured();
            SyncNowButton.IsEnabled = CloudSyncToggle.IsOn && configured && !_syncService.IsSyncing;
            TestSyncButton.IsEnabled = CloudSyncToggle.IsOn && configured;
        }

        private bool IsSyncConfigured()
        {
            var settings = UserSettings.Instance;
            if (string.Equals(settings.CloudSyncBackend, "CloudflareR2", StringComparison.OrdinalIgnoreCase))
            {
                return !string.IsNullOrWhiteSpace(settings.R2Endpoint) &&
                       !string.IsNullOrWhiteSpace(settings.R2Bucket) &&
                       !string.IsNullOrWhiteSpace(settings.R2AccessKeyId) &&
                       !string.IsNullOrWhiteSpace(SecretProtector.Decrypt(settings.R2SecretAccessKey));
            }
            return !string.IsNullOrWhiteSpace(settings.SyncFolderPath) &&
                   Directory.Exists(settings.SyncFolderPath);
        }

        private void UpdateSyncStatus()
        {
            if (_syncService.IsSyncing)
            {
                SyncStatusText.Text = "正在同步...";
            }
            else
            {
                var settings = UserSettings.Instance;
                if (settings.LastCloudSyncTime != null)
                {
                    SyncStatusText.Text = $"上次同步: {settings.LastCloudSyncTime:yyyy-MM-dd HH:mm:ss}" +
                                          (string.IsNullOrEmpty(settings.LastCloudSyncSummary)
                                              ? string.Empty
                                              : $"\n{settings.LastCloudSyncSummary}");
                }
                else
                {
                    SyncStatusText.Text = "尚未执行过同步。";
                }
            }

            var history = _syncService.GetHistory();
            SyncHistoryText.Text = history.Count == 0
                ? string.Empty
                : string.Join("\n", history.Take(3).Select(h =>
                    $"[{h.TimeUtc.ToLocalTime():MM-dd HH:mm}] {h.Summary}"));
        }

        private void OnSyncProgressChanged(SyncProgress progress)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!_syncUiLoaded) return;
                SyncStatusText.Text = $"正在同步 ({progress.CompletedActions}/{progress.TotalActions}): {progress.CurrentFile}";
            });
        }

        private void OnSyncCompleted(SyncResult result)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!_syncUiLoaded) return;
                UpdateSyncStatus();
                UpdateCloudSyncPanels();
            });
        }

        private void OnSettingsDialogClosed(ContentDialog sender, ContentDialogClosedEventArgs args)
        {
            _syncService.ProgressChanged -= OnSyncProgressChanged;
            _syncService.SyncCompleted -= OnSyncCompleted;
            _syncUiLoaded = false;
        }

        private async void ExportDataButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var fileSavePicker = new FileSavePicker();
                fileSavePicker.SuggestedStartLocation = PickerLocationId.Desktop;
                fileSavePicker.FileTypeChoices.Add("GameLauncher 备份文件", new List<string> { ".gldata" });
                fileSavePicker.SuggestedFileName = $"GameLauncher_Backup_{DateTime.Now:yyyyMMdd}";
                WinRT.Interop.InitializeWithWindow.Initialize(fileSavePicker,
                    WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));

                var file = await fileSavePicker.PickSaveFileAsync();
                if (file == null) return;

                var service = App.Services.GetRequiredService<DataExportImportService>();

                var success = await service.ExportAsync(file.Path);
                if (success)
                {
                    if (App.MainWindow is MainWindow mainWindow)
                    {
                        mainWindow.ShowToast("导出成功", "数据已成功导出到备份文件", ToastType.Success);
                    }
                }
                else
                {
                    if (App.MainWindow is MainWindow mainWindow)
                    {
                        mainWindow.ShowToast("导出失败", "导出数据时发生错误", ToastType.Error);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"导出数据失败: {ex.Message}");
            }
        }

        private async void ImportDataButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 先关闭设置弹窗，避免嵌套 ContentDialog 导致 COMException
                this.Hide();

                var confirmDialog = new ContentDialog
                {
                    Title = "确认导入",
                    Content = "导入数据将覆盖现有的所有游戏数据，此操作不可撤销。确定要继续吗？",
                    PrimaryButtonText = "确认导入",
                    CloseButtonText = "取消",
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = App.MainWindow?.Content?.XamlRoot
                };

                var result = await confirmDialog.ShowAsync();
                if (result != ContentDialogResult.Primary)
                {
                    await ReopenSettingsDialog();
                    return;
                }

                var fileOpenPicker = new FileOpenPicker();
                fileOpenPicker.SuggestedStartLocation = PickerLocationId.Desktop;
                fileOpenPicker.FileTypeFilter.Add(".gldata");
                WinRT.Interop.InitializeWithWindow.Initialize(fileOpenPicker,
                    WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));

                var file = await fileOpenPicker.PickSingleFileAsync();
                if (file == null)
                {
                    await ReopenSettingsDialog();
                    return;
                }

                var service = App.Services.GetRequiredService<DataExportImportService>();

                if (!service.ValidateImportFile(file.Path))
                {
                    if (App.MainWindow is MainWindow mainWindow)
                    {
                        mainWindow.ShowToast("导入失败", "文件格式不正确", ToastType.Error);
                    }
                    await ReopenSettingsDialog();
                    return;
                }

                var success = await service.ImportAsync(file.Path);
                if (success)
                {
                    if (App.MainWindow is MainWindow mainWindow)
                    {
                        mainWindow.ShowToast("导入成功", "数据已成功导入", ToastType.Success);
                        await mainWindow.RefreshGameListAsync();
                    }
                }
                else
                {
                    if (App.MainWindow is MainWindow mainWindow)
                    {
                        mainWindow.ShowToast("导入失败", "导入数据时发生错误", ToastType.Error);
                    }
                }
                await ReopenSettingsDialog();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"导入数据失败: {ex.Message}");
                try { await ReopenSettingsDialog(); } catch { }
            }
        }

        private async void SetPrivatePasswordButton_Click(object sender, RoutedEventArgs e)
        {
            // 先关闭设置弹窗，再弹出密码录入弹窗（避免嵌套 ContentDialog）
            this.Hide();

            // 第一次输入
            var firstKeys = await ShowPasswordRecordingDialog("设置私密模式密码", "按顺序按下键盘按键作为密码，完成后点击保存。");
            if (firstKeys == null || firstKeys.Count == 0)
            {
                await ReopenSettingsDialog();
                return;
            }

            // 第二次确认输入
            var secondKeys = await ShowPasswordRecordingDialog("确认密码", "请再次输入相同的按键序列以确认。");
            if (secondKeys == null || secondKeys.Count == 0)
            {
                await ReopenSettingsDialog();
                return;
            }

            // 比对两次输入
            if (firstKeys.Count != secondKeys.Count || !firstKeys.SequenceEqual(secondKeys))
            {
                if (App.MainWindow is MainWindow mainWindow)
                {
                    mainWindow.ShowToast("密码不匹配", "两次输入的按键序列不一致，请重新设置", ToastType.Error);
                }
                await ReopenSettingsDialog();
                return;
            }

            PrivateModeService.Instance.SetKeySequence(firstKeys);
            if (App.MainWindow is MainWindow mw)
            {
                mw.ShowToast("密码已设置", "私密模式密码已更新", ToastType.Success);
            }

            await ReopenSettingsDialog();
        }

        private async Task<List<int>?> ShowPasswordRecordingDialog(string title, string prompt)
        {
            var dialog = new ContentDialog
            {
                Title = title,
                PrimaryButtonText = "保存",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.None,
                XamlRoot = App.MainWindow?.Content?.XamlRoot
            };

            var recordedKeys = new List<int>();
            var displayText = new TextBlock
            {
                Text = "请按下键盘按键序列...",
                FontSize = 16,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Microsoft.UI.Xaml.Thickness(0, 8, 0, 8)
            };

            var stackPanel = new StackPanel();
            stackPanel.Children.Add(new TextBlock
            {
                Text = prompt,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Microsoft.UI.Xaml.Thickness(0, 0, 0, 8)
            });
            stackPanel.Children.Add(displayText);
            dialog.Content = stackPanel;

            // 使用 PreviewKeyDown 拦截 Enter，防止触发保存按钮关闭弹窗
            dialog.AddHandler(UIElement.PreviewKeyDownEvent, new Microsoft.UI.Xaml.Input.KeyEventHandler((dlg, args) =>
            {
                if (args.Key == Windows.System.VirtualKey.Enter)
                {
                    recordedKeys.Add((int)args.Key);
                    displayText.Text = string.Join(" ", recordedKeys.Select(k => PrivateModeService.KeyCodeToDisplayString(k)));
                    args.Handled = true;
                }
            }), handledEventsToo: true);

            // 使用 AddHandler 强制捕获所有其他按键
            dialog.AddHandler(UIElement.KeyDownEvent, new Microsoft.UI.Xaml.Input.KeyEventHandler((dlg, args) =>
            {
                recordedKeys.Add((int)args.Key);
                displayText.Text = string.Join(" ", recordedKeys.Select(k => PrivateModeService.KeyCodeToDisplayString(k)));
                args.Handled = true;
            }), handledEventsToo: true);

            var result = await dialog.ShowAsync();
            return result == ContentDialogResult.Primary && recordedKeys.Count > 0 ? recordedKeys : null;
        }

        private async Task ReopenSettingsDialog()
        {
            var settingsDialog = new SettingsDialog
            {
                XamlRoot = App.MainWindow?.Content?.XamlRoot
            };
            await settingsDialog.ShowAsync();
        }

        private void ResetPrivatePasswordButton_Click(object sender, RoutedEventArgs e)
        {
            PrivateModeService.Instance.ResetToDefault();
            UpdatePrivatePasswordButtonText();
            if (App.MainWindow is MainWindow mainWindow)
            {
                mainWindow.ShowToast("密码已重置", "私密模式密码已重置为默认密码", ToastType.Success);
            }
        }
    }
}
