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
        public List<int> PrivateKeySequence { get; set; } = new List<int>();
        public bool DebugModeEnabled { get; set; } = false;

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
                System.Diagnostics.Debug.WriteLine($"设置已保存: {json}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"保存设置失败: {ex.Message}");
            }
        }
    }
}
