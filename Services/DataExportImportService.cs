using GameLauncher.Data;
using GameLauncher.Models;
using System.Collections.ObjectModel;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;

namespace GameLauncher.Services
{
    public class GameExportEntry
    {
        public string Name { get; set; } = string.Empty;
        public string ExecutablePath { get; set; } = string.Empty;
        public string GameId { get; set; } = string.Empty;
        public string? IconPath { get; set; }
        public string? Description { get; set; }
        public int LaunchCount { get; set; }
        public long TotalPlayTime { get; set; }
        public DateTime? LastRunTime { get; set; }
        public bool IsPrivate { get; set; }
        public string Tags { get; set; } = string.Empty;
        public string ImagePaths { get; set; } = string.Empty;
        public string CollectionNames { get; set; } = string.Empty;
    }

    public class SettingsExport
    {
        public bool HideUnavailableGames { get; set; }
        public bool AutoScanEnabled { get; set; }
        public bool DebugModeEnabled { get; set; }
        public List<string> ScanPaths { get; set; } = new List<string>();
        public List<int> PrivateKeySequence { get; set; } = new List<int>();
    }

    public class GameDataExport
    {
        public string Version { get; set; } = "3.4.1";
        public DateTime ExportDate { get; set; }
        public int GameCount { get; set; }
        public List<GameExportEntry> Games { get; set; } = new List<GameExportEntry>();
        public List<string> CollectionNames { get; set; } = new List<string>();
    }

    public class DataExportImportService
    {
        private readonly DatabaseContext _dbContext;
        private readonly GameRepository _gameRepository;
        private readonly ImageService _imageService;
        private readonly CollectionRepository _collectionRepo;

        private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = null
        };

        public DataExportImportService(DatabaseContext dbContext, GameRepository gameRepository, ImageService imageService, CollectionRepository collectionRepo)
        {
            _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
            _gameRepository = gameRepository ?? throw new ArgumentNullException(nameof(gameRepository));
            _imageService = imageService ?? throw new ArgumentNullException(nameof(imageService));
            _collectionRepo = collectionRepo ?? throw new ArgumentNullException(nameof(collectionRepo));
        }

        public async Task<bool> ExportAsync(string filePath)
        {
            var tempDir = Path.Combine(Path.GetTempPath(), $"gldata_export_{Guid.NewGuid():N}");
            try
            {
                Directory.CreateDirectory(tempDir);

                // 1. 导出游戏数据
                var games = await _gameRepository.GetAllGamesAsync();
                var export = new GameDataExport
                {
                    Version = "3.4.2",
                    ExportDate = DateTime.UtcNow,
                    GameCount = games.Count,
                    Games = games.Select(g => new GameExportEntry
                    {
                        Name = g.Name,
                        ExecutablePath = g.ExecutablePath,
                        GameId = g.GameId,
                        IconPath = g.IconPath,
                        Description = g.Description,
                        LaunchCount = g.LaunchCount,
                        TotalPlayTime = g.TotalPlayTime,
                        LastRunTime = g.LastRunTime,
                        IsPrivate = g.IsPrivate,
                        Tags = SerializeStringList(g.Tags),
                        ImagePaths = SerializeStringList(g.ImagePaths),
                        CollectionNames = SerializeStringList(new ObservableCollection<string>(g.Collections.Select(c => c.Name).ToList()))
                    }).ToList()
                };

                // 导出收藏夹定义
                var allCollections = await _collectionRepo.GetAllCollectionsAsync();
                export.CollectionNames = allCollections.Select(c => c.Name).ToList();

                var dataJson = JsonSerializer.Serialize(export, _jsonOptions);
                await File.WriteAllTextAsync(Path.Combine(tempDir, "data.json"), dataJson);

                // 2. 导出设置信息
                var settings = UserSettings.Instance;
                var settingsExport = new SettingsExport
                {
                    HideUnavailableGames = settings.HideUnavailableGames,
                    AutoScanEnabled = settings.AutoScanEnabled,
                    DebugModeEnabled = settings.DebugModeEnabled,
                    ScanPaths = settings.ScanPaths?.ToList() ?? new List<string>(),
                    PrivateKeySequence = settings.PrivateKeySequence?.ToList() ?? new List<int>()
                };
                var settingsJson = JsonSerializer.Serialize(settingsExport, _jsonOptions);
                await File.WriteAllTextAsync(Path.Combine(tempDir, "settings.json"), settingsJson);

                // 3. 导出游戏图片
                var imagesDir = Path.Combine(tempDir, "images");
                Directory.CreateDirectory(imagesDir);

                foreach (var game in games)
                {
                    var gameDir = _imageService.GetGameDirectory(game.GameId);
                    if (!Directory.Exists(gameDir)) continue;

                    var destGameDir = Path.Combine(imagesDir, game.GameId);
                    Directory.CreateDirectory(destGameDir);

                    foreach (var file in Directory.GetFiles(gameDir))
                    {
                        var fileName = Path.GetFileName(file);
                        var destPath = Path.Combine(destGameDir, fileName);
                        File.Copy(file, destPath, overwrite: true);
                    }
                }

                // 4. 创建 ZIP 压缩包
                if (File.Exists(filePath))
                    File.Delete(filePath);

                ZipFile.CreateFromDirectory(tempDir, filePath, CompressionLevel.Optimal, includeBaseDirectory: false);

                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DataExportImportService.ExportAsync failed: {ex.Message}");
                return false;
            }
            finally
            {
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
            }
        }

        public async Task<bool> ImportAsync(string filePath)
        {
            var tempDir = Path.Combine(Path.GetTempPath(), $"gldata_import_{Guid.NewGuid():N}");
            try
            {
                if (!File.Exists(filePath))
                    return false;

                // 1. 解压 ZIP
                ZipFile.ExtractToDirectory(filePath, tempDir);

                // 2. 读取游戏数据
                var dataJsonPath = Path.Combine(tempDir, "data.json");
                if (!File.Exists(dataJsonPath))
                    return false;

                var json = await File.ReadAllTextAsync(dataJsonPath);
                var export = JsonSerializer.Deserialize<GameDataExport>(json, _jsonOptions);

                if (export == null || export.Games == null)
                    return false;

                // 3. 导入游戏数据到数据库
                using var connection = await _dbContext.GetOpenConnectionAsync();

                var hasIsPrivate = await ColumnExistsAsync(connection, "Games", "IsPrivate");

                using var transaction = connection.BeginTransaction();
                try
                {
                    using (var delCmd1 = connection.CreateCommand())
                    {
                        delCmd1.Transaction = transaction;
                        delCmd1.CommandText = "DELETE FROM GameCollectionItems";
                        await delCmd1.ExecuteNonQueryAsync();
                    }
                    using (var delCmd2 = connection.CreateCommand())
                    {
                        delCmd2.Transaction = transaction;
                        delCmd2.CommandText = "DELETE FROM ConsistencyCheckLog";
                        await delCmd2.ExecuteNonQueryAsync();
                    }
                    using (var delCmd3 = connection.CreateCommand())
                    {
                        delCmd3.Transaction = transaction;
                        delCmd3.CommandText = "DELETE FROM Games";
                        await delCmd3.ExecuteNonQueryAsync();
                    }

                    // 为每个游戏分配递增的时间戳，保证导入后按时间排序顺序正确
                    var baseTime = DateTime.UtcNow;
                    int gameIndex = 0;
                    var gameIdToNewId = new Dictionary<string, int>();
                    // 跟踪每个条目实际使用的 GameId，用于后续收藏夹关联映射
                    var entryActualGameIds = new List<string>();
                    foreach (var entry in export.Games)
                    {
                        using var insertCmd = connection.CreateCommand();
                        insertCmd.Transaction = transaction;

                        var columns = new List<string>
                        {
                            "GameId", "Name", "ExecutablePath", "IconPath", "Description",
                            "CreatedAt", "LaunchCount", "TotalPlayTime", "LastRunTime",
                            "ImagePaths", "Tags"
                        };
                        if (hasIsPrivate) columns.Add("IsPrivate");

                        var paramNames = new List<string>();
                        for (int i = 0; i < columns.Count; i++)
                            paramNames.Add($"@p{i}");

                        insertCmd.CommandText = $"INSERT INTO Games ({string.Join(", ", columns)}) VALUES ({string.Join(", ", paramNames)}); SELECT last_insert_rowid();";

                        var gameId = string.IsNullOrWhiteSpace(entry.GameId) ? GenerateGameId() : entry.GameId;
                        entryActualGameIds.Add(gameId);
                        insertCmd.Parameters.AddWithValue("@p0", gameId);
                        insertCmd.Parameters.AddWithValue("@p1", entry.Name);
                        insertCmd.Parameters.AddWithValue("@p2", entry.ExecutablePath);
                        insertCmd.Parameters.AddWithValue("@p3", string.IsNullOrEmpty(entry.IconPath) ? (object)DBNull.Value : entry.IconPath);
                        insertCmd.Parameters.AddWithValue("@p4", string.IsNullOrEmpty(entry.Description) ? (object)DBNull.Value : entry.Description);
                        insertCmd.Parameters.AddWithValue("@p5", baseTime.AddSeconds(-gameIndex).ToString("o"));
                        insertCmd.Parameters.AddWithValue("@p6", entry.LaunchCount);
                        insertCmd.Parameters.AddWithValue("@p7", entry.TotalPlayTime);
                        insertCmd.Parameters.AddWithValue("@p8", entry.LastRunTime.HasValue ? entry.LastRunTime.Value.ToString("o") : (object)DBNull.Value);
                        insertCmd.Parameters.AddWithValue("@p9", string.IsNullOrEmpty(entry.ImagePaths) ? (object)DBNull.Value : entry.ImagePaths);
                        insertCmd.Parameters.AddWithValue("@p10", string.IsNullOrEmpty(entry.Tags) ? (object)DBNull.Value : entry.Tags);
                        if (hasIsPrivate)
                            insertCmd.Parameters.AddWithValue("@p11", entry.IsPrivate ? 1 : 0);

                        var newGameId = Convert.ToInt32(await insertCmd.ExecuteScalarAsync());
                        gameIdToNewId[gameId] = newGameId;
                        gameIndex++;
                    }

                    // 恢复收藏夹数据
                    if (export.CollectionNames != null && export.CollectionNames.Count > 0)
                    {
                        // 插入收藏夹定义
                        var collectionNameToId = new Dictionary<string, int>();
                        foreach (var colName in export.CollectionNames)
                        {
                            using var insertColCmd = connection.CreateCommand();
                            insertColCmd.Transaction = transaction;
                            insertColCmd.CommandText = "INSERT OR IGNORE INTO GameCollections (Name, CreatedAt) VALUES (@Name, @CreatedAt)";
                            insertColCmd.Parameters.AddWithValue("@Name", colName);
                            insertColCmd.Parameters.AddWithValue("@CreatedAt", DateTime.UtcNow);
                            await insertColCmd.ExecuteNonQueryAsync();
                        }

                        // 获取所有收藏夹 ID
                        using var selectColsCmd = connection.CreateCommand();
                        selectColsCmd.Transaction = transaction;
                        selectColsCmd.CommandText = "SELECT Id, Name FROM GameCollections";
                        using var colReader = await selectColsCmd.ExecuteReaderAsync();
                        while (await colReader.ReadAsync())
                        {
                            collectionNameToId[colReader.GetString(1)] = colReader.GetInt32(0);
                        }

                        // 插入游戏-收藏夹关联
                        int entryIndex = 0;
                        foreach (var entry in export.Games)
                        {
                            var collectionNames = DeserializeStringList(entry.CollectionNames);
                            if (collectionNames == null || collectionNames.Count == 0) { entryIndex++; continue; }

                            // 使用插入阶段实际使用的 GameId 进行查找，确保一致性
                            var actualGameId = entryIndex < entryActualGameIds.Count ? entryActualGameIds[entryIndex] : null;
                            var gameKey = actualGameId ?? (string.IsNullOrWhiteSpace(entry.GameId) ? GenerateGameId() : entry.GameId);
                            if (!gameIdToNewId.TryGetValue(gameKey, out var existingGameId))
                            { entryIndex++; continue; }

                            foreach (var colName in collectionNames)
                            {
                                if (!collectionNameToId.TryGetValue(colName, out var colId)) continue;

                                using var insertMapCmd = connection.CreateCommand();
                                insertMapCmd.Transaction = transaction;
                                insertMapCmd.CommandText = "INSERT OR IGNORE INTO GameCollectionItems (GameId, CollectionId) VALUES (@GameId, @CollectionId)";
                                insertMapCmd.Parameters.AddWithValue("@GameId", existingGameId);
                                insertMapCmd.Parameters.AddWithValue("@CollectionId", colId);
                                await insertMapCmd.ExecuteNonQueryAsync();
                            }
                            entryIndex++;
                        }
                    }

                    transaction.Commit();
                }
                catch
                {
                    try { transaction.Rollback(); } catch { }
                    throw;
                }

                // 4. 恢复设置信息
                var settingsJsonPath = Path.Combine(tempDir, "settings.json");
                if (File.Exists(settingsJsonPath))
                {
                    try
                    {
                        var settingsJson = await File.ReadAllTextAsync(settingsJsonPath);
                        var settingsExport = JsonSerializer.Deserialize<SettingsExport>(settingsJson, _jsonOptions);
                        if (settingsExport != null)
                        {
                            var settings = UserSettings.Instance;
                            settings.HideUnavailableGames = settingsExport.HideUnavailableGames;
                            settings.AutoScanEnabled = settingsExport.AutoScanEnabled;
                            settings.DebugModeEnabled = settingsExport.DebugModeEnabled;
                            settings.ScanPaths = settingsExport.ScanPaths ?? new List<string>();
                            settings.PrivateKeySequence = settingsExport.PrivateKeySequence ?? new List<int>();
                            settings.Save();
                            PrivateModeService.Instance.ReloadFromSettings();
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"DataExportImportService: 恢复设置失败: {ex.Message}");
                    }
                }

                // 5. 恢复游戏图片
                var imagesDir = Path.Combine(tempDir, "images");
                if (Directory.Exists(imagesDir))
                {
                    foreach (var gameDir in Directory.GetDirectories(imagesDir))
                    {
                        var gameId = Path.GetFileName(gameDir);
                        var destGameDir = _imageService.GetGameDirectory(gameId);
                        if (!Directory.Exists(destGameDir))
                            Directory.CreateDirectory(destGameDir);

                        foreach (var file in Directory.GetFiles(gameDir))
                        {
                            var fileName = Path.GetFileName(file);
                            var destPath = Path.Combine(destGameDir, fileName);
                            File.Copy(file, destPath, overwrite: true);
                        }
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DataExportImportService.ImportAsync failed: {ex.Message}");
                return false;
            }
            finally
            {
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
            }
        }

        public bool ValidateImportFile(string filePath)
        {
            try
            {
                if (!File.Exists(filePath))
                    return false;

                // 验证是否为有效的 ZIP 文件
                using var zip = ZipFile.OpenRead(filePath);
                var dataEntry = zip.GetEntry("data.json");
                if (dataEntry == null)
                    return false;

                using var stream = dataEntry.Open();
                using var reader = new StreamReader(stream);
                var json = reader.ReadToEnd();
                var export = JsonSerializer.Deserialize<GameDataExport>(json, _jsonOptions);

                return export != null && export.Games != null;
            }
            catch
            {
                return false;
            }
        }

        private static string SerializeStringList(System.Collections.ObjectModel.ObservableCollection<string> items)
        {
            if (items == null || items.Count == 0) return string.Empty;
            try { return JsonSerializer.Serialize(items.ToList()); }
            catch { return string.Empty; }
        }

        private static List<string>? DeserializeStringList(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try { return JsonSerializer.Deserialize<List<string>>(json); }
            catch { return null; }
        }

        private static async Task<bool> ColumnExistsAsync(SqliteConnection connection, string tableName, string columnName)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"PRAGMA table_info({tableName})";
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static string GenerateGameId()
        {
            var digits = new byte[9];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(digits);
            }
            var gid = "GID";
            for (int i = 0; i < 9; i++)
                gid += (digits[i] % 10).ToString();
            return gid;
        }
    }
}