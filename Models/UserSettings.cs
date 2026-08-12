using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace GameLauncher.Models
{
    public class UserSettings
    {
        private static UserSettings? _instance;
        private static readonly object _lock = new object();
        private static string SettingsFilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GameLauncher",
            "settings.json");

        public bool HideUnavailableGames { get; set; } = false;
        public bool AutoScanEnabled { get; set; } = false;
        public List<string> ScanPaths { get; set; } = new List<string>();
        /// <summary>游戏目录（Steam 式：启动时识别该目录下的游戏，下载的游戏解压到此）</summary>
        public string GameLibraryPath { get; set; } = string.Empty;
        public List<int> PrivateKeySequence { get; set; } = new List<int>();
        public bool DebugModeEnabled { get; set; } = false;

        // ---- 游戏云备份（123 云盘）----
        public string Pan123ClientId { get; set; } = string.Empty;
        public string Pan123ClientSecret { get; set; } = string.Empty;
        public string Pan123AccessToken { get; set; } = string.Empty;
        public DateTime? Pan123TokenExpiry { get; set; }
        /// <summary>云端归档根目录 ID（首次归档自动创建并记住，0=根目录）</summary>
        public long Pan123ParentFolderId { get; set; } = 0;

        // ---- 增量云同步 ----
        public bool CloudSyncEnabled { get; set; } = false;
        public string CloudSyncBackend { get; set; } = "Folder";
        public string SyncFolderPath { get; set; } = string.Empty;
        public string R2Endpoint { get; set; } = "https://<ACCOUNT_ID>.r2.cloudflarestorage.com";
        public string R2Bucket { get; set; } = string.Empty;
        public string R2AccessKeyId { get; set; } = string.Empty;
        public string R2SecretAccessKey { get; set; } = string.Empty;
        public string CloudSyncDirection { get; set; } = "TwoWay";
        public int CloudSyncIntervalMinutes { get; set; } = 0;
        public DateTime? LastCloudSyncTime { get; set; }
        public string? LastCloudSyncSummary { get; set; }

        [System.Text.Json.Serialization.JsonIgnore]
        public bool HasPrivatePassword
        {
            get => PrivateKeySequence != null && PrivateKeySequence.Count > 0;
        }

        public static UserSettings Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                        {
                            _instance = LoadSettings();
                        }
                    }
                }
                return _instance;
            }
        }

        public UserSettings() { }

        private static UserSettings LoadSettings()
        {
            try
            {
                if (File.Exists(SettingsFilePath))
                {
                    var json = File.ReadAllText(SettingsFilePath);
                    var settings = JsonSerializer.Deserialize<UserSettings>(json, new JsonSerializerOptions
                    {
                        IncludeFields = true
                    });
                    if (settings != null)
                    {
                        if (settings.ScanPaths == null)
                        {
                            settings.ScanPaths = new List<string>();
                        }
                        if (settings.PrivateKeySequence == null)
                        {
                            settings.PrivateKeySequence = new List<int>();
                        }
                        // 旧版本自动扫描多路径 → 单游戏目录迁移：取第一个存在的路径
                        if (string.IsNullOrWhiteSpace(settings.GameLibraryPath) && settings.ScanPaths.Count > 0)
                        {
                            settings.GameLibraryPath = settings.ScanPaths[0];
                        }
                        return settings;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"加载设置失败: {ex.Message}");
            }
            return new UserSettings();
        }

        public void Save()
        {
            try
            {
                var directory = Path.GetDirectoryName(SettingsFilePath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var options = new JsonSerializerOptions
                {
                    WriteIndented = true
                };
                var json = JsonSerializer.Serialize(this, options);
                File.WriteAllText(SettingsFilePath, json);
                AppendSaveLog($"Save() 成功: GameLibraryPath='{GameLibraryPath}' AutoScan={AutoScanEnabled}");
            }
            catch (Exception ex)
            {
                AppendSaveLog($"Save() 失败: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void AppendSaveLog(string message)
        {
            try
            {
                var logPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "GameLauncher", "settings_log.txt");
                File.AppendAllText(logPath, $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
            }
            catch { }
        }
    }
}
