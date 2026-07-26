using GameLauncher.Data;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace GameLauncher.Services
{
    public class DebugStats
    {
        public int TotalGames { get; set; }
        public int TotalCollections { get; set; }
        public string DatabaseSize { get; set; } = string.Empty;
        public string DatabasePath { get; set; } = string.Empty;
    }

    public class DebugLogService
    {
        private readonly DatabaseContext _dbContext;
        private readonly List<(DateTime Timestamp, string Message)> _logEntries = new();
        private readonly object _lock = new();
        private const int MaxEntries = 500;

        public DebugLogService(DatabaseContext dbContext)
        {
            _dbContext = dbContext;
        }

        public void Log(string message)
        {
            lock (_lock)
            {
                _logEntries.Add((DateTime.Now, message));

                while (_logEntries.Count > MaxEntries)
                {
                    _logEntries.RemoveAt(0);
                }
            }

            System.Diagnostics.Debug.WriteLine($"[DebugLog] {DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}");
        }

        public List<(DateTime Timestamp, string Message)> GetRecentLogs(int count = 50)
        {
            lock (_lock)
            {
                return _logEntries
                    .AsEnumerable()
                    .Reverse()
                    .Take(count)
                    .Reverse()
                    .ToList();
            }
        }

        public DebugStats GetDatabaseStats()
        {
            var stats = new DebugStats
            {
                DatabasePath = _dbContext.DatabasePath
            };

            try
            {
                using var connection = _dbContext.GetConnection();
                connection.Open();

                using var cmdGames = connection.CreateCommand();
                cmdGames.CommandText = "SELECT COUNT(*) FROM Games";
                var result = cmdGames.ExecuteScalar();
                stats.TotalGames = result is long l ? (int)l : Convert.ToInt32(result ?? 0);

                using var cmdCollections = connection.CreateCommand();
                cmdCollections.CommandText = "SELECT COUNT(*) FROM GameCollections";
                result = cmdCollections.ExecuteScalar();
                stats.TotalCollections = result is long l2 ? (int)l2 : Convert.ToInt32(result ?? 0);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DebugLog] GetDatabaseStats query failed: {ex.Message}");
            }

            try
            {
                var fileInfo = new FileInfo(_dbContext.DatabasePath);
                if (fileInfo.Exists)
                {
                    stats.DatabaseSize = FormatFileSize(fileInfo.Length);
                }
                else
                {
                    stats.DatabaseSize = "N/A";
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DebugLog] GetDatabaseStats file size failed: {ex.Message}");
                stats.DatabaseSize = "N/A";
            }

            return stats;
        }

        public void ClearLogs()
        {
            lock (_lock)
            {
                _logEntries.Clear();
            }
        }

        private static string FormatFileSize(long bytes)
        {
            string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
            int suffixIndex = 0;
            double size = bytes;

            while (size >= 1024 && suffixIndex < suffixes.Length - 1)
            {
                size /= 1024;
                suffixIndex++;
            }

            return suffixIndex == 0
                ? $"{size:F0} {suffixes[suffixIndex]}"
                : $"{size:F1} {suffixes[suffixIndex]}";
        }
    }
}