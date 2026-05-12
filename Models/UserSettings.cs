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
