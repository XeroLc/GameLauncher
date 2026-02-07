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
            var appDataPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GameLauncher");
            
            _databasePath = Path.Combine(appDataPath, "games.db");
        }

        public string DatabasePath => _databasePath;

        public SqliteConnection GetConnection()
        {
            return new SqliteConnection($"Data Source={_databasePath}");
        }

        public async Task InitializeAsync()
        {
            var needsNewColumns = false;
            
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
                        IsRunning INTEGER DEFAULT 0
                    )";
                
                await command.ExecuteNonQueryAsync();
            }
            else
            {
                using var connection = GetConnection();
                await connection.OpenAsync();

                using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA table_info(Games)";
                
                using var reader = await command.ExecuteReaderAsync();
                var columns = new System.Collections.Generic.List<string>();
                while (await reader.ReadAsync())
                {
                    columns.Add(reader.GetString(1));
                }

                if (!columns.Contains("LaunchCount"))
                {
                    needsNewColumns = true;
                }
            }

            if (needsNewColumns)
            {
                await AddMissingColumnsAsync();
            }
        }

        private async System.Threading.Tasks.Task AddMissingColumnsAsync()
        {
            using var connection = GetConnection();
            await connection.OpenAsync();

            var columnsToAdd = new[]
            {
                "LaunchCount INTEGER DEFAULT 0",
                "TotalPlayTime INTEGER DEFAULT 0",
                "LastRunTime DATETIME",
                "IsRunning INTEGER DEFAULT 0"
            };

            foreach (var column in columnsToAdd)
            {
                using var command = connection.CreateCommand();
                command.CommandText = $"ALTER TABLE Games ADD COLUMN {column}";
                try
                {
                    await command.ExecuteNonQueryAsync();
                }
                catch
                {
                    // Column might already exist
                }
            }
        }
    }
}