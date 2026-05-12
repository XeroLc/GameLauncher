using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

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
                            _instance = new UserSettings();
                            _instance.Load();
                        }
                    }
                }
                return _instance;
            }
        }

        private UserSettings() { }

        private void Load()
        {
            try
            {
                if (File.Exists(SettingsFilePath))
                {
                    var json = File.ReadAllText(SettingsFilePath);
                    var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("hideUnavailableGames", out var prop))
                    {
                        HideUnavailableGames = prop.GetBoolean();
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"加载设置失败: {ex.Message}");
            }
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

                var json = $"{{\"hideUnavailableGames\": {HideUnavailableGames.ToString().ToLowerInvariant()}}}";
                File.WriteAllText(SettingsFilePath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"保存设置失败: {ex.Message}");
            }
        }
    }
}
