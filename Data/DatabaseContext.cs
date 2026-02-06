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
                        CreatedAt DATETIME DEFAULT CURRENT_TIMESTAMP
                    )";
                
                await command.ExecuteNonQueryAsync();
            }
        }
    }
}