using Microsoft.Data.Sqlite;
using System;
using System.IO;
using System.Threading.Tasks;

namespace GameLauncher.Data
{
    public class DatabaseContext
    {
        private readonly string _databasePath;

        public DatabaseContext()
            {
                // 尝试使用 Windows.Storage.ApplicationData 获取一致的本地数据目录
                string appDataPath;
                try
                {
                    var localFolder = Windows.Storage.ApplicationData.Current.LocalFolder.Path;
                    appDataPath = localFolder;
                }
                catch
                {
                    // 如果不可用（例如在某些特殊运行环境下），回退到传统方式
                    appDataPath = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "GameLauncher");
                }
                
                _databasePath = Path.Combine(appDataPath, "games.db");
            }
        public string DatabasePath => _databasePath;

        public SqliteConnection GetConnection()
        {
            return new SqliteConnection($"Data Source={_databasePath}");
        }

        public async Task InitializeAsync()
        {
            var dir = Path.GetDirectoryName(_databasePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                await Task.Run(() => Directory.CreateDirectory(dir));
            }

            var fileExists = await Task.Run(() => File.Exists(_databasePath));

            if (!fileExists)
            {
                using var connection = GetConnection();
                await connection.OpenAsync();

                using var command = connection.CreateCommand();
                command.CommandText = @"
                    CREATE TABLE Games (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        Name TEXT NOT NULL,
                        ExecutablePath TEXT NOT NULL,
                        IconPath TEXT,
                        Description TEXT,
                        CreatedAt DATETIME DEFAULT CURRENT_TIMESTAMP,
                        LaunchCount INTEGER DEFAULT 0,
                        TotalPlayTime INTEGER DEFAULT 0,
                        LastRunTime DATETIME,
                        IsRunning INTEGER DEFAULT 0,
                        IsFavorite INTEGER DEFAULT 0,
                        ImagePaths TEXT,
                        Tags TEXT
                    )";

                await command.ExecuteNonQueryAsync();
            }
            else
            {
                await AddMissingColumnsAsync();
            }
        }

        private async System.Threading.Tasks.Task AddMissingColumnsAsync()
        {
            using var connection = GetConnection();
            await connection.OpenAsync();

            // 先获取现有列
            var existingColumns = new System.Collections.Generic.List<string>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA table_info(Games)";
                using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    existingColumns.Add(reader.GetString(1));
                }
            }

            // 只添加不存在的列
            var columnsToAdd = new[]
            {
                ("LaunchCount", "INTEGER DEFAULT 0"),
                ("TotalPlayTime", "INTEGER DEFAULT 0"),
                ("LastRunTime", "DATETIME"),
                ("IsRunning", "INTEGER DEFAULT 0"),
                ("IsFavorite", "INTEGER DEFAULT 0"),
                ("ImagePaths", "TEXT"),
                ("Tags", "TEXT")
            };

            foreach (var (columnName, columnDefinition) in columnsToAdd)
            {
                if (!existingColumns.Contains(columnName))
                {
                    using var command = connection.CreateCommand();
                    command.CommandText = $"ALTER TABLE Games ADD COLUMN {columnName} {columnDefinition}";
                    try
                    {
                        await command.ExecuteNonQueryAsync();
                        System.Diagnostics.Debug.WriteLine($"成功添加列: {columnName}");
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"添加列 {columnName} 失败: {ex.Message}");
                        // 忽略错误，列可能已经存在
                    }
                }
            }
        }
    }
}