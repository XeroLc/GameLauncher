using GameLauncher.Models;
using GameLauncher.Services;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GameLauncher.Data
{
    public class GameRepository
    {
        private readonly DatabaseContext _context;
        private readonly GmdFileService _gmdService;
        private readonly CollectionRepository _collectionRepo;
        private readonly ImageService _imageService;
        private Dictionary<string, int>? _cachedColumnMap;
        private readonly SemaphoreSlim _columnMapLock = new(1, 1);

        public GameRepository(DatabaseContext context, GmdFileService gmdService, CollectionRepository collectionRepo, ImageService imageService)
        {
            _context = context;
            _gmdService = gmdService;
            _collectionRepo = collectionRepo;
            _imageService = imageService;
        }

        private string SerializeImagePaths(ObservableCollection<string> imagePaths)
        {
            if (imagePaths == null || imagePaths.Count == 0) return string.Empty;
            try { return JsonSerializer.Serialize(imagePaths.ToList()); }
            catch { return string.Empty; }
        }

        private string SerializeTags(ObservableCollection<string> tags)
        {
            if (tags == null || tags.Count == 0) return string.Empty;
            try { return JsonSerializer.Serialize(tags.ToList()); }
            catch { return string.Empty; }
        }

        public async Task<List<Game>> GetAllGamesAsync()
        {
            try
            {
                return await GetAllGamesInternalAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"GetAllGamesAsync 完整查询失败，尝试降级: {ex.Message}");
                try
                {
                    return await GetAllGamesFallbackAsync();
                }
                catch (Exception fallbackEx)
                {
                    System.Diagnostics.Debug.WriteLine($"GetAllGamesAsync 降级查询也失败: {fallbackEx.Message}");
                    return new List<Game>();
                }
            }
        }

        private async Task<List<Game>> GetAllGamesInternalAsync()
        {
            var games = new List<Game>();

            using var connection = _context.GetConnection();
            await connection.OpenAsync();

            var columnMap = await GetColumnMapAsync(connection);

            var selectList = new List<string> { "Id", "Name", "ExecutablePath" };
            if (columnMap.ContainsKey("GameId")) selectList.Add("GameId");
            if (columnMap.ContainsKey("IconPath")) selectList.Add("IconPath");
            if (columnMap.ContainsKey("Description")) selectList.Add("Description");
            if (columnMap.ContainsKey("CreatedAt")) selectList.Add("CreatedAt");
            if (columnMap.ContainsKey("LaunchCount")) selectList.Add("LaunchCount");
            if (columnMap.ContainsKey("TotalPlayTime")) selectList.Add("TotalPlayTime");
            if (columnMap.ContainsKey("LastRunTime")) selectList.Add("LastRunTime");
            if (columnMap.ContainsKey("IsRunning")) selectList.Add("IsRunning");
            if (columnMap.ContainsKey("ImagePaths")) selectList.Add("ImagePaths");
            if (columnMap.ContainsKey("Tags")) selectList.Add("Tags");
            if (columnMap.ContainsKey("IsPrivate")) selectList.Add("IsPrivate");

            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT {string.Join(", ", selectList)} FROM Games ORDER BY CreatedAt DESC";

            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var game = new Game
                {
                    Id = reader.GetInt32(0),
                    Name = reader.GetString(1),
                    ExecutablePath = reader.GetString(2)
                };

                int idx = 3;
                if (columnMap.ContainsKey("GameId"))
                {
                    game.GameId = reader.IsDBNull(idx) ? string.Empty : reader.GetString(idx);
                    idx++;
                }
                if (columnMap.ContainsKey("IconPath"))
                {
                    game.IconPath = reader.IsDBNull(idx) ? null : reader.GetString(idx);
                    idx++;
                }
                if (columnMap.ContainsKey("Description"))
                {
                    game.Description = reader.IsDBNull(idx) ? null : reader.GetString(idx);
                    idx++;
                }
                if (columnMap.ContainsKey("CreatedAt"))
                {
                    game.CreatedAt = reader.IsDBNull(idx) ? DateTime.MinValue : reader.GetDateTime(idx);
                    idx++;
                }
                if (columnMap.ContainsKey("LaunchCount"))
                {
                    game.LaunchCount = reader.IsDBNull(idx) ? 0 : reader.GetInt32(idx);
                    idx++;
                }
                if (columnMap.ContainsKey("TotalPlayTime"))
                {
                    game.TotalPlayTime = reader.IsDBNull(idx) ? 0 : reader.GetInt64(idx);
                    idx++;
                }
                if (columnMap.ContainsKey("LastRunTime"))
                {
                    game.LastRunTime = reader.IsDBNull(idx) ? null : reader.GetDateTime(idx);
                    idx++;
                }
                if (columnMap.ContainsKey("IsRunning"))
                {
                    game.IsRunning = !reader.IsDBNull(idx) && reader.GetInt32(idx) == 1;
                    idx++;
                }

                if (columnMap.ContainsKey("ImagePaths") && !reader.IsDBNull(idx))
                {
                    try
                    {
                        var imagePathsJson = reader.GetString(idx);
                        var imagePaths = JsonSerializer.Deserialize<List<string>>(imagePathsJson);
                        if (imagePaths != null)
                        {
                            foreach (var path in imagePaths)
                                game.ImagePaths.Add(path);
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"反序列化 ImagePaths 失败: {ex.Message}");
                    }
                }
                if (columnMap.ContainsKey("ImagePaths")) idx++;

                if (columnMap.ContainsKey("Tags") && !reader.IsDBNull(idx))
                {
                    try
                    {
                        var tagsJson = reader.GetString(idx);
                        var tags = JsonSerializer.Deserialize<List<string>>(tagsJson);
                        if (tags != null)
                        {
                            foreach (var tag in tags)
                                game.Tags.Add(tag);
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"反序列化 Tags 失败: {ex.Message}");
                    }
                }
                if (columnMap.ContainsKey("Tags")) idx++;

                if (columnMap.ContainsKey("IsPrivate") && !reader.IsDBNull(idx))
                {
                    game.IsPrivate = reader.GetInt32(idx) == 1;
                }
                if (columnMap.ContainsKey("IsPrivate")) idx++;

                // 设置.gmd文件路径信息（仅用于备份/锚点标记）
                try
                {
                    if (!string.IsNullOrWhiteSpace(game.GameId))
                    {
                        var gmdPath = _gmdService.GetGmdFilePath(game.ExecutablePath, game.GameId);
                        game.GmdFilePath = gmdPath;
                        game.IsGmdFileReady = _gmdService.GmdFileExists(gmdPath);

                        // 数据库数据缺失时回退到.gmd文件
                        if (game.IsGmdFileReady)
                        {
                            var needGmdFallback = string.IsNullOrEmpty(game.Description) ||
                                                  string.IsNullOrEmpty(game.IconPath) ||
                                                  game.ImagePaths.Count == 0;
                            game.NeedsGmdFallback = needGmdFallback;
                        }
                    }
                    else
                    {
                        // GameId 尚未分配（旧数据库升级场景），跳过 GMD 检查
                        game.IsGmdFileReady = false;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"获取.gmd路径失败 {game.Name}: {ex.Message}");
                    game.IsGmdFileReady = false;
                }

                games.Add(game);
            }

            try
            {
                var collectionMappings = await _collectionRepo.GetAllGameCollectionMappingsAsync();
                foreach (var game in games)
                {
                    if (collectionMappings.TryGetValue(game.Id, out var collections))
                    {
                        foreach (var col in collections)
                        {
                            if (!game.Collections.Any(c => c.Id == col.Id))
                                game.Collections.Add(col);
                        }
                    }
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"批量加载收藏夹失败: {ex.Message}"); }

            return games;
        }

        private async Task<Dictionary<string, int>> GetColumnMapAsync(SqliteConnection connection)
        {
            if (_cachedColumnMap != null)
                return _cachedColumnMap;

            await _columnMapLock.WaitAsync();
            try
            {
                // 双重检查锁定
                if (_cachedColumnMap != null)
                    return _cachedColumnMap;

                var columnMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                using var pragmaCommand = connection.CreateCommand();
                pragmaCommand.CommandText = "PRAGMA table_info(Games)";
                using var pragmaReader = await pragmaCommand.ExecuteReaderAsync();
                while (await pragmaReader.ReadAsync())
                {
                    var colName = pragmaReader.GetString(1);
                    if (!columnMap.ContainsKey(colName))
                        columnMap[colName] = pragmaReader.GetInt32(0);
                }
                _cachedColumnMap = columnMap;
                return columnMap;
            }
            finally
            {
                _columnMapLock.Release();
            }
        }

        private async Task<List<Game>> GetAllGamesFallbackAsync()
        {
            var games = new List<Game>();

            using var connection = _context.GetConnection();
            await connection.OpenAsync();

            using var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT Id, Name, ExecutablePath
                FROM Games
                ORDER BY ID DESC";

            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var game = new Game
                {
                    Id = reader.GetInt32(0),
                    Name = reader.GetString(1),
                    ExecutablePath = reader.GetString(2),
                    Description = "",
                    CreatedAt = DateTime.MinValue
                };
                games.Add(game);
            }

            return games;
        }

        /// <summary>
        /// 轻量结算游戏时长：单语句原子更新，避免全字段 UPDATE + 全量重建 .gmd 文件
        /// </summary>
        public async Task<bool> UpdateGamePlayTimeAsync(int gameId, long delta)
        {
            using var connection = _context.GetConnection();
            await connection.OpenAsync();

            using var command = connection.CreateCommand();
            command.CommandText = @"
                UPDATE Games
                SET TotalPlayTime = TotalPlayTime + @Delta,
                    IsRunning = 0,
                    LastRunTime = @LastRunTime
                WHERE Id = @Id";
            command.Parameters.AddWithValue("@Delta", delta);
            command.Parameters.AddWithValue("@LastRunTime", DateTime.UtcNow);
            command.Parameters.AddWithValue("@Id", gameId);

            int rowsAffected = await command.ExecuteNonQueryAsync();
            return rowsAffected > 0;
        }

        public async Task<Game?> GetGameByIdAsync(int id)
        {
            using var connection = _context.GetConnection();
            await connection.OpenAsync();

            using var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT Id, GameId, Name, ExecutablePath, IconPath, Description, CreatedAt,
                       LaunchCount, TotalPlayTime, LastRunTime, IsRunning, ImagePaths, Tags, IsPrivate
                FROM Games
                WHERE Id = @Id";
            command.Parameters.AddWithValue("@Id", id);

            using var reader = await command.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                var game = new Game
                {
                    Id = reader.GetInt32(0),
                    GameId = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                    Name = reader.GetString(2),
                    ExecutablePath = reader.GetString(3),
                    IconPath = reader.IsDBNull(4) ? null : reader.GetString(4),
                    Description = reader.IsDBNull(5) ? null : reader.GetString(5),
                    CreatedAt = reader.GetDateTime(6),
                    LaunchCount = reader.IsDBNull(7) ? 0 : reader.GetInt32(7),
                    TotalPlayTime = reader.IsDBNull(8) ? 0 : reader.GetInt64(8),
                    LastRunTime = reader.IsDBNull(9) ? null : reader.GetDateTime(9),
                    IsRunning = !reader.IsDBNull(10) && reader.GetInt32(10) == 1
                };

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

                game.IsPrivate = !reader.IsDBNull(13) && reader.GetInt32(13) == 1;

                try
                {
                    var collections = await _collectionRepo.GetCollectionsForGameAsync(game.Id);
                    foreach (var col in collections)
                    {
                        if (!game.Collections.Any(c => c.Id == col.Id))
                            game.Collections.Add(col);
                    }
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"加载游戏 {game.Name} 的收藏夹失败: {ex.Message}"); }

                return game;
            }

            return null;
        }

        public async Task<int> AddGameAsync(Game game)
        {
            using var connection = _context.GetConnection();
            await connection.OpenAsync();

            if (string.IsNullOrWhiteSpace(game.GameId))
            {
                game.GameId = GenerateUniqueGameId(connection);
            }

            using var command = connection.CreateCommand();
            command.CommandText = @"
                INSERT INTO Games (GameId, Name, ExecutablePath, IconPath, Description, CreatedAt, ImagePaths, Tags, IsPrivate)
                VALUES (@GameId, @Name, @ExecutablePath, @IconPath, @Description, @CreatedAt, @ImagePaths, @Tags, @IsPrivate);
                SELECT last_insert_rowid();";

            command.Parameters.AddWithValue("@GameId", game.GameId ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@Name", game.Name);
            command.Parameters.AddWithValue("@ExecutablePath", game.ExecutablePath);
            command.Parameters.AddWithValue("@IconPath", game.IconPath ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@Description", game.Description ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@CreatedAt", DateTime.UtcNow);

            var imagePathsJson = SerializeImagePaths(game.ImagePaths);
            command.Parameters.AddWithValue("@ImagePaths", string.IsNullOrEmpty(imagePathsJson) ? (object)DBNull.Value : imagePathsJson);

            var tagsJson = SerializeTags(game.Tags);
            command.Parameters.AddWithValue("@Tags", string.IsNullOrEmpty(tagsJson) ? (object)DBNull.Value : tagsJson);

            command.Parameters.AddWithValue("@IsPrivate", game.IsPrivate ? 1 : 0);

            var result = await command.ExecuteScalarAsync();
            int gameId = Convert.ToInt32(result);

            // 创建.gmd文件
            try
            {
                game.Id = gameId;
                game.GmdFilePath = _gmdService.GetGmdFilePath(game.ExecutablePath, game.GameId);
                await _gmdService.SerializeGameToGmdAsync(game, _imageService);
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
                SET GameId = @GameId,
                    Name = @Name,
                    ExecutablePath = @ExecutablePath,
                    IconPath = @IconPath,
                    Description = @Description,
                    LaunchCount = @LaunchCount,
                    TotalPlayTime = @TotalPlayTime,
                    LastRunTime = @LastRunTime,
                    IsRunning = @IsRunning,
                    ImagePaths = @ImagePaths,
                    Tags = @Tags,
                    IsPrivate = @IsPrivate
                WHERE Id = @Id";

            command.Parameters.AddWithValue("@GameId", game.GameId ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@Name", game.Name);
            command.Parameters.AddWithValue("@ExecutablePath", game.ExecutablePath);
            command.Parameters.AddWithValue("@IconPath", game.IconPath ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@Description", game.Description ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@LaunchCount", game.LaunchCount);
            command.Parameters.AddWithValue("@TotalPlayTime", game.TotalPlayTime);
            command.Parameters.AddWithValue("@LastRunTime", game.LastRunTime ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@IsRunning", game.IsRunning ? 1 : 0);
            command.Parameters.AddWithValue("@IsPrivate", game.IsPrivate ? 1 : 0);
            command.Parameters.AddWithValue("@Id", game.Id);

            var imagePathsJson = SerializeImagePaths(game.ImagePaths);
            command.Parameters.AddWithValue("@ImagePaths", string.IsNullOrEmpty(imagePathsJson) ? (object)DBNull.Value : imagePathsJson);

            var tagsJson = SerializeTags(game.Tags);
            command.Parameters.AddWithValue("@Tags", string.IsNullOrEmpty(tagsJson) ? (object)DBNull.Value : tagsJson);

            int rowsAffected = await command.ExecuteNonQueryAsync();

            // 更新.gmd文件
            if (rowsAffected > 0)
            {
                try
                {
                    if (string.IsNullOrEmpty(game.GmdFilePath))
                    {
                        game.GmdFilePath = _gmdService.GetGmdFilePath(game.ExecutablePath, game.GameId);
                    }
                    
                    await _gmdService.SerializeGameToGmdAsync(game, _imageService);
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

        public async Task<bool> DeleteGameAsync(int gameId, bool deleteGmd = true)
        {
            var game = await GetGameByIdAsync(gameId);

            using var connection = _context.GetConnection();
            await connection.OpenAsync();

            using var transaction = connection.BeginTransaction();
            try
            {
                if (game != null)
                {
                    try
                    {
                        using var delCmd = connection.CreateCommand();
                        delCmd.Transaction = transaction;
                        delCmd.CommandText = "DELETE FROM GameCollectionItems WHERE GameId = @Id";
                        delCmd.Parameters.AddWithValue("@Id", gameId);
                        await delCmd.ExecuteNonQueryAsync();
                    }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"删除游戏集合关联失败: {ex.Message}"); }

                    try
                    {
                        using var delLogCmd = connection.CreateCommand();
                        delLogCmd.Transaction = transaction;
                        delLogCmd.CommandText = "DELETE FROM ConsistencyCheckLog WHERE GameId = @Id";
                        delLogCmd.Parameters.AddWithValue("@Id", gameId);
                        await delLogCmd.ExecuteNonQueryAsync();
                    }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"删除一致性检查日志失败: {ex.Message}"); }
                }

                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "DELETE FROM Games WHERE Id = @Id";
                command.Parameters.AddWithValue("@Id", gameId);
                int rowsAffected = await command.ExecuteNonQueryAsync();

                transaction.Commit();

                if (rowsAffected > 0 && game != null)
                {
                    try
                    {
                        if (!string.IsNullOrWhiteSpace(game.GameId))
                        {
                            _imageService.DeleteGameImages(game.GameId);
                        }
                    }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"删除游戏图片目录失败: {ex.Message}"); }

                    // Delete GMD file only if requested
                    if (deleteGmd)
                    {
                        try
                        {
                            if (!string.IsNullOrEmpty(game.GmdFilePath) && File.Exists(game.GmdFilePath))
                            {
                                File.Delete(game.GmdFilePath);
                            }
                            else if (!string.IsNullOrWhiteSpace(game.GameId))
                            {
                                // Try to find GMD file by GameId pattern
                                var gmdPath = _gmdService.GetGmdFilePath(game.ExecutablePath, game.GameId);
                                if (File.Exists(gmdPath))
                                    File.Delete(gmdPath);
                            }
                        }
                        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"删除GMD文件失败: {ex.Message}"); }
                    }
                }

                return rowsAffected > 0;
            }
            catch
            {
                try { transaction.Rollback(); } catch { }
                throw;
            }
        }

        public async Task<int> DeleteGamesAsync(IEnumerable<int> ids, bool deleteGmd = true)
        {
            var idList = ids.ToList();
            if (idList.Count == 0) return 0;

            var gamesToDelete = new List<Game>();
            foreach (var id in idList)
            {
                var game = await GetGameByIdAsync(id);
                if (game != null) gamesToDelete.Add(game);
            }

            using var connection = _context.GetConnection();
            await connection.OpenAsync();

            using var transaction = connection.BeginTransaction();
            try
            {
                using var delItemsCmd = connection.CreateCommand();
                delItemsCmd.Transaction = transaction;
                var itemParams = string.Join(",", idList.Select((_, i) => $"@Id{i}"));
                delItemsCmd.CommandText = $"DELETE FROM GameCollectionItems WHERE GameId IN ({itemParams})";
                for (int i = 0; i < idList.Count; i++)
                    delItemsCmd.Parameters.AddWithValue($"@Id{i}", idList[i]);
                await delItemsCmd.ExecuteNonQueryAsync();

                using var delLogCmd = connection.CreateCommand();
                delLogCmd.Transaction = transaction;
                delLogCmd.CommandText = $"DELETE FROM ConsistencyCheckLog WHERE GameId IN ({itemParams})";
                for (int i = 0; i < idList.Count; i++)
                    delLogCmd.Parameters.AddWithValue($"@Id{i}", idList[i]);
                await delLogCmd.ExecuteNonQueryAsync();

                using var delGamesCmd = connection.CreateCommand();
                delGamesCmd.Transaction = transaction;
                delGamesCmd.CommandText = $"DELETE FROM Games WHERE Id IN ({itemParams})";
                for (int i = 0; i < idList.Count; i++)
                    delGamesCmd.Parameters.AddWithValue($"@Id{i}", idList[i]);
                int rowsAffected = await delGamesCmd.ExecuteNonQueryAsync();

                transaction.Commit();

                foreach (var game in gamesToDelete)
                {
                    try
                    {
                        if (!string.IsNullOrWhiteSpace(game.GameId))
                        {
                            _imageService.DeleteGameImages(game.GameId);
                        }
                    }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"删除游戏图片目录失败: {ex.Message}"); }

                    // Delete GMD file only if requested
                    if (deleteGmd)
                    {
                        try
                        {
                            if (!string.IsNullOrEmpty(game.GmdFilePath) && File.Exists(game.GmdFilePath))
                            {
                                File.Delete(game.GmdFilePath);
                            }
                            else if (!string.IsNullOrWhiteSpace(game.GameId))
                            {
                                // Try to find GMD file by GameId pattern
                                var gmdPath = _gmdService.GetGmdFilePath(game.ExecutablePath, game.GameId);
                                if (File.Exists(gmdPath))
                                    File.Delete(gmdPath);
                            }
                        }
                        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"删除GMD文件失败: {ex.Message}"); }
                    }
                }

                return rowsAffected;
            }
            catch
            {
                try { transaction.Rollback(); } catch { }
                throw;
            }
        }

        public async Task<bool> GameIdExistsAsync(string gameId)
        {
            using var connection = _context.GetConnection();
            await connection.OpenAsync();

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(1) FROM Games WHERE GameId = @GameId";
            command.Parameters.AddWithValue("@GameId", gameId);

            var result = await command.ExecuteScalarAsync();
            return result != null && Convert.ToInt64(result) > 0;
        }

        private string GenerateUniqueGameId(Microsoft.Data.Sqlite.SqliteConnection connection)
        {
            const int maxRetries = 10;
            for (int attempt = 0; attempt < maxRetries; attempt++)
            {
                var digits = new byte[9];
                using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create())
                {
                    rng.GetBytes(digits);
                }
                var gid = "GID";
                for (int i = 0; i < 9; i++)
                {
                    gid += (digits[i] % 10).ToString();
                }

                using var checkCmd = connection.CreateCommand();
                checkCmd.CommandText = "SELECT COUNT(1) FROM Games WHERE GameId = @GameId";
                checkCmd.Parameters.AddWithValue("@GameId", gid);
                var result = checkCmd.ExecuteScalar();
                if (result == null || Convert.ToInt64(result) == 0)
                {
                    return gid;
                }
            }

            throw new InvalidOperationException($"无法在 {maxRetries} 次尝试内生成唯一的 GameId");
        }
    }
}