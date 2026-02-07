using GameLauncher.Models;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace GameLauncher.Data
{
    public class GameRepository
    {
        private readonly DatabaseContext _context;

        public GameRepository(DatabaseContext context)
        {
            _context = context;
        }

        public async Task<List<Game>> GetAllGamesAsync()
        {
            var games = new List<Game>();

            using var connection = _context.GetConnection();
            await connection.OpenAsync();

            using var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT Id, Name, ExecutablePath, IconPath, Description, CreatedAt, 
                       LaunchCount, TotalPlayTime, LastRunTime, IsRunning 
                FROM Games 
                ORDER BY CreatedAt DESC";

            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                games.Add(new Game
                {
                    Id = reader.GetInt32(0),
                    Name = reader.GetString(1),
                    ExecutablePath = reader.GetString(2),
                    IconPath = reader.IsDBNull(3) ? null : reader.GetString(3),
                    Description = reader.IsDBNull(4) ? null : reader.GetString(4),
                    CreatedAt = reader.GetDateTime(5),
                    LaunchCount = reader.IsDBNull(6) ? 0 : reader.GetInt32(6),
                    TotalPlayTime = reader.IsDBNull(7) ? 0 : reader.GetInt64(7),
                    LastRunTime = reader.IsDBNull(8) ? null : reader.GetDateTime(8),
                    IsRunning = !reader.IsDBNull(9) && reader.GetInt32(9) == 1
                });
            }

            return games;
        }

        public async Task<Game?> GetGameByIdAsync(int id)
        {
            using var connection = _context.GetConnection();
            await connection.OpenAsync();

            using var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT Id, Name, ExecutablePath, IconPath, Description, CreatedAt, 
                       LaunchCount, TotalPlayTime, LastRunTime, IsRunning 
                FROM Games 
                WHERE Id = @Id";
            command.Parameters.AddWithValue("@Id", id);

            using var reader = await command.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                return new Game
                {
                    Id = reader.GetInt32(0),
                    Name = reader.GetString(1),
                    ExecutablePath = reader.GetString(2),
                    IconPath = reader.IsDBNull(3) ? null : reader.GetString(3),
                    Description = reader.IsDBNull(4) ? null : reader.GetString(4),
                    CreatedAt = reader.GetDateTime(5),
                    LaunchCount = reader.IsDBNull(6) ? 0 : reader.GetInt32(6),
                    TotalPlayTime = reader.IsDBNull(7) ? 0 : reader.GetInt64(7),
                    LastRunTime = reader.IsDBNull(8) ? null : reader.GetDateTime(8),
                    IsRunning = !reader.IsDBNull(9) && reader.GetInt32(9) == 1
                };
            }

            return null;
        }

        public async Task<int> AddGameAsync(Game game)
        {
            using var connection = _context.GetConnection();
            await connection.OpenAsync();

            using var command = connection.CreateCommand();
            command.CommandText = @"
                INSERT INTO Games (Name, ExecutablePath, IconPath, Description, CreatedAt)
                VALUES (@Name, @ExecutablePath, @IconPath, @Description, @CreatedAt);
                SELECT last_insert_rowid();";
            
            command.Parameters.AddWithValue("@Name", game.Name);
            command.Parameters.AddWithValue("@ExecutablePath", game.ExecutablePath);
            command.Parameters.AddWithValue("@IconPath", game.IconPath ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@Description", game.Description ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@CreatedAt", DateTime.UtcNow);

            var result = await command.ExecuteScalarAsync();
            return Convert.ToInt32(result);
        }

        public async Task<bool> UpdateGameAsync(Game game)
        {
            using var connection = _context.GetConnection();
            await connection.OpenAsync();

            using var command = connection.CreateCommand();
            command.CommandText = @"
                UPDATE Games 
                SET Name = @Name, 
                    ExecutablePath = @ExecutablePath, 
                    IconPath = @IconPath, 
                    Description = @Description,
                    LaunchCount = @LaunchCount,
                    TotalPlayTime = @TotalPlayTime,
                    LastRunTime = @LastRunTime,
                    IsRunning = @IsRunning
                WHERE Id = @Id";
            
            command.Parameters.AddWithValue("@Name", game.Name);
            command.Parameters.AddWithValue("@ExecutablePath", game.ExecutablePath);
            command.Parameters.AddWithValue("@IconPath", game.IconPath ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@Description", game.Description ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@LaunchCount", game.LaunchCount);
            command.Parameters.AddWithValue("@TotalPlayTime", game.TotalPlayTime);
            command.Parameters.AddWithValue("@LastRunTime", game.LastRunTime ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@IsRunning", game.IsRunning ? 1 : 0);
            command.Parameters.AddWithValue("@Id", game.Id);

            int rowsAffected = await command.ExecuteNonQueryAsync();
            return rowsAffected > 0;
        }

        public async Task<bool> DeleteGameAsync(int id)
        {
            using var connection = _context.GetConnection();
            await connection.OpenAsync();

            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM Games WHERE Id = @Id";
            command.Parameters.AddWithValue("@Id", id);

            int rowsAffected = await command.ExecuteNonQueryAsync();
            return rowsAffected > 0;
        }
    }
}