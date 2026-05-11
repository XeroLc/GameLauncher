using GameLauncher.Models;
using GameLauncher.Services;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace GameLauncher.Data
{
    public class GameRepository
    {
        private readonly DatabaseContext _context;
        private readonly GmdFileService _gmdService;

        public GameRepository(DatabaseContext context)
        {
            _context = context;
            _gmdService = new GmdFileService();
        }

        public async Task<List<Game>> GetAllGamesAsync()
        {
            var games = new List<Game>();

            using var connection = _context.GetConnection();
            await connection.OpenAsync();

            using var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT Id, Name, ExecutablePath, IconPath, Description, CreatedAt,
                       LaunchCount, TotalPlayTime, LastRunTime, IsRunning, IsFavorite, ImagePaths, Tags
                FROM Games
                ORDER BY CreatedAt DESC";

            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var game = new Game
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
                    IsRunning = !reader.IsDBNull(9) && reader.GetInt32(9) == 1,
                    IsFavorite = !reader.IsDBNull(10) && reader.GetInt32(10) == 1
                };

                // 反序列化 ImagePaths
                if (!reader.IsDBNull(11))
                {
                    try
                    {
                        var imagePathsJson = reader.GetString(11);
                        var imagePaths = JsonSerializer.Deserialize<List<string>>(imagePathsJson);
                        if (imagePaths != null)
                        {
                            foreach (var path in imagePaths)
                            {
                                game.ImagePaths.Add(path);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"反序列化 ImagePaths 失败: {ex.Message}");
                    }
                }

                // 反序列化 Tags
                if (!reader.IsDBNull(12))
                {
                    try
                    {
                        var tagsJson = reader.GetString(12);
                        var tags = JsonSerializer.Deserialize<List<string>>(tagsJson);
                        if (tags != null)
                        {
                            foreach (var tag in tags)
                            {
                                game.Tags.Add(tag);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"反序列化 Tags 失败: {ex.Message}");
                    }
                }

                // 设置.gmd文件路径信息（仅用于备份/锚点标记）
                try
                {
                    var gmdPath = _gmdService.GetGmdFilePath(game.ExecutablePath, game.Name);
                    game.GmdFilePath = gmdPath;
                    game.IsGmdFileReady = _gmdService.GmdFileExists(gmdPath);

                    // 数据库数据缺失时回退到.gmd文件
                    if (game.IsGmdFileReady)
                    {
                        var needGmdFallback = string.IsNullOrEmpty(game.Description) ||
                                              string.IsNullOrEmpty(game.IconPath) ||
                                              game.ImagePaths.Count == 0;

                        if (needGmdFallback)
                        {
                            try
                            {
                                var gmdGame = await _gmdService.DeserializeGameFromGmdAsync(gmdPath);
                                if (gmdGame != null)
                                {
                                    if (string.IsNullOrEmpty(game.Description) && !string.IsNullOrEmpty(gmdGame.Description))
                                        game.Description = gmdGame.Description;
                                    if (string.IsNullOrEmpty(game.IconPath) && !string.IsNullOrEmpty(gmdGame.IconPath))
                                        game.IconPath = gmdGame.IconPath;
                                    if (game.ImagePaths.Count == 0 && gmdGame.ImagePaths.Count > 0)
                                    {
                                        foreach (var path in gmdGame.ImagePaths)
                                        {
                                            game.ImagePaths.Add(path);
                                        }
                                    }
                                    if (game.Tags.Count == 0 && gmdGame.Tags.Count > 0)
                                    {
                                        foreach (var tag in gmdGame.Tags)
                                        {
                                            game.Tags.Add(tag);
                                        }
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"从.gmd回退加载游戏 {game.Name} 失败: {ex.Message}");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"获取.gmd路径失败 {game.Name}: {ex.Message}");
                    game.IsGmdFileReady = false;
                }

                games.Add(game);
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
                       LaunchCount, TotalPlayTime, LastRunTime, IsRunning, IsFavorite, ImagePaths, Tags
                FROM Games
                WHERE Id = @Id";
            command.Parameters.AddWithValue("@Id", id);

            using var reader = await command.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                var game = new Game
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
                    IsRunning = !reader.IsDBNull(9) && reader.GetInt32(9) == 1,
                    IsFavorite = !reader.IsDBNull(10) && reader.GetInt32(10) == 1
                };

                // 反序列化 ImagePaths
                if (!reader.IsDBNull(11))
                {
                    try
                    {
                        var imagePathsJson = reader.GetString(11);
                        var imagePaths = JsonSerializer.Deserialize<List<string>>(imagePathsJson);
                        if (imagePaths != null)
                        {
                            foreach (var path in imagePaths)
                            {
                                game.ImagePaths.Add(path);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"反序列化 ImagePaths 失败: {ex.Message}");
                        // 忽略反序列化错误
                    }
                }

                // 反序列化 Tags
                if (!reader.IsDBNull(12))
                {
                    try
                    {
                        var tagsJson = reader.GetString(12);
                        var tags = JsonSerializer.Deserialize<List<string>>(tagsJson);
                        if (tags != null)
                        {
                            foreach (var tag in tags)
                            {
                                game.Tags.Add(tag);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"反序列化 Tags 失败: {ex.Message}");
                        // 忽略反序列化错误
                    }
                }

                return game;
            }

            return null;
        }

        public async Task<int> AddGameAsync(Game game)
        {
            using var connection = _context.GetConnection();
            await connection.OpenAsync();

            using var command = connection.CreateCommand();
            command.CommandText = @"
                INSERT INTO Games (Name, ExecutablePath, IconPath, Description, CreatedAt, ImagePaths, Tags)
                VALUES (@Name, @ExecutablePath, @IconPath, @Description, @CreatedAt, @ImagePaths, @Tags);
                SELECT last_insert_rowid();";

            command.Parameters.AddWithValue("@Name", game.Name);
            command.Parameters.AddWithValue("@ExecutablePath", game.ExecutablePath);
            command.Parameters.AddWithValue("@IconPath", game.IconPath ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@Description", game.Description ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@CreatedAt", DateTime.UtcNow);

            // 序列化 ImagePaths
            string imagePathsJson = string.Empty;
            if (game.ImagePaths != null && game.ImagePaths.Count > 0)
            {
                try
                {
                    imagePathsJson = JsonSerializer.Serialize(game.ImagePaths.ToList());
                }
                catch
                {
                }
            }
            command.Parameters.AddWithValue("@ImagePaths", string.IsNullOrEmpty(imagePathsJson) ? (object)DBNull.Value : imagePathsJson);

            // 序列化 Tags
            string tagsJson = string.Empty;
            if (game.Tags != null && game.Tags.Count > 0)
            {
                try
                {
                    tagsJson = JsonSerializer.Serialize(game.Tags.ToList());
                }
                catch
                {
                }
            }
            command.Parameters.AddWithValue("@Tags", string.IsNullOrEmpty(tagsJson) ? (object)DBNull.Value : tagsJson);

            var result = await command.ExecuteScalarAsync();
            int gameId = Convert.ToInt32(result);

            // 创建.gmd文件
            try
            {
                game.Id = gameId;
                game.GmdFilePath = _gmdService.GetGmdFilePath(game.ExecutablePath, game.Name);
                await _gmdService.SerializeGameToGmdAsync(game);
                game.IsGmdFileReady = true;
                System.Diagnostics.Debug.WriteLine($"创建.gmd文件成功: {game.GmdFilePath}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"创建.gmd文件失败: {ex.Message}");
                // 不抛出异常，允许数据库操作成功而.gmd失败
            }

            return gameId;
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
                    IsRunning = @IsRunning,
                    IsFavorite = @IsFavorite,
                    ImagePaths = @ImagePaths,
                    Tags = @Tags
                WHERE Id = @Id";

            command.Parameters.AddWithValue("@Name", game.Name);
            command.Parameters.AddWithValue("@ExecutablePath", game.ExecutablePath);
            command.Parameters.AddWithValue("@IconPath", game.IconPath ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@Description", game.Description ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@LaunchCount", game.LaunchCount);
            command.Parameters.AddWithValue("@TotalPlayTime", game.TotalPlayTime);
            command.Parameters.AddWithValue("@LastRunTime", game.LastRunTime ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@IsRunning", game.IsRunning ? 1 : 0);
            command.Parameters.AddWithValue("@IsFavorite", game.IsFavorite ? 1 : 0);
            command.Parameters.AddWithValue("@Id", game.Id);

            // 序列化 ImagePaths
            string imagePathsJson = string.Empty;
            if (game.ImagePaths != null && game.ImagePaths.Count > 0)
            {
                try
                {
                    imagePathsJson = JsonSerializer.Serialize(game.ImagePaths.ToList());
                }
                catch
                {
                }
            }
            command.Parameters.AddWithValue("@ImagePaths", string.IsNullOrEmpty(imagePathsJson) ? (object)DBNull.Value : imagePathsJson);

            // 序列化 Tags
            string tagsJson = string.Empty;
            if (game.Tags != null && game.Tags.Count > 0)
            {
                try
                {
                    tagsJson = JsonSerializer.Serialize(game.Tags.ToList());
                }
                catch
                {
                }
            }
            command.Parameters.AddWithValue("@Tags", string.IsNullOrEmpty(tagsJson) ? (object)DBNull.Value : tagsJson);

            int rowsAffected = await command.ExecuteNonQueryAsync();

            // 更新.gmd文件
            if (rowsAffected > 0)
            {
                try
                {
                    if (string.IsNullOrEmpty(game.GmdFilePath))
                    {
                        game.GmdFilePath = _gmdService.GetGmdFilePath(game.ExecutablePath, game.Name);
                    }
                    
                    await _gmdService.SerializeGameToGmdAsync(game);
                    game.IsGmdFileReady = true;
                    System.Diagnostics.Debug.WriteLine($"更新.gmd文件成功: {game.GmdFilePath}");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"更新.gmd文件失败: {ex.Message}");
                }
            }

            return rowsAffected > 0;
        }

        public async Task<bool> DeleteGameAsync(int id)
        {
            // 先获取游戏信息以便删除.gmd文件
            var game = await GetGameByIdAsync(id);
            
            using var connection = _context.GetConnection();
            await connection.OpenAsync();

            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM Games WHERE Id = @Id";
            command.Parameters.AddWithValue("@Id", id);

            int rowsAffected = await command.ExecuteNonQueryAsync();

            // 删除.gmd文件
            if (rowsAffected > 0 && game != null)
            {
                try
                {
                    var gmdPath = !string.IsNullOrEmpty(game.GmdFilePath)
                        ? game.GmdFilePath
                        : _gmdService.GetGmdFilePath(game.ExecutablePath, game.Name);
                    
                    _gmdService.DeleteGmdFile(gmdPath);
                    System.Diagnostics.Debug.WriteLine($"删除.gmd文件成功: {gmdPath}");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"删除.gmd文件失败: {ex.Message}");
                }
            }

            return rowsAffected > 0;
        }
    }
}