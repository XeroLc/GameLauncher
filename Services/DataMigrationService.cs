using GameLauncher.Data;
using GameLauncher.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace GameLauncher.Services
{
    public class MigrationProgress
    {
        public int CurrentGameIndex { get; set; }
        public int TotalGames { get; set; }
        public string CurrentGameName { get; set; } = string.Empty;
        public double Percentage { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    public class MigrationStatus
    {
        public int TotalGames { get; set; }
        public int MigratedGames { get; set; }
        public int PendingGames { get; set; }
        public int FailedGames { get; set; }
        public List<MigrationDetail> MigrationDetails { get; set; } = new List<MigrationDetail>();
    }

    public class MigrationDetail
    {
        public int GameId { get; set; }
        public string GameName { get; set; } = string.Empty;
        public MigrationResult Result { get; set; }
        public string Message { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; }
    }

    public enum MigrationResult
    {
        Pending,
        Success,
        Failed,
        AlreadyMigrated
    }

    public class DataMigrationService
    {
        private readonly GmdFileService _gmdFileService;
        private readonly DatabaseContext _dbContext;

        public DataMigrationService(GmdFileService gmdFileService, DatabaseContext dbContext)
        {
            _gmdFileService = gmdFileService ?? throw new ArgumentNullException(nameof(gmdFileService));
            _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        }

        public Task<List<Game>> ScanForMissingGmdFilesAsync(IEnumerable<Game> games)
        {
            if (games == null)
                throw new ArgumentNullException(nameof(games));

            return Task.Run(() =>
            {
                var missingGames = new List<Game>();

                foreach (var game in games)
                {
                    if (game == null)
                        continue;

                    if (string.IsNullOrWhiteSpace(game.ExecutablePath) || string.IsNullOrWhiteSpace(game.Name) || string.IsNullOrWhiteSpace(game.GameId))
                    {
                        Debug.WriteLine($"[DataMigrationService] 跳过无效游戏数据: ID={game.Id}, GameId={(string.IsNullOrWhiteSpace(game.GameId) ? "为空" : "有效")}");
                        continue;
                    }

                    var gmdFilePath = _gmdFileService.GetGmdFilePath(game.ExecutablePath, game.GameId);
                    if (!_gmdFileService.GmdFileExists(gmdFilePath))
                    {
                        missingGames.Add(game);
                        Debug.WriteLine($"[DataMigrationService] 发现缺少.gmd文件的游戏: {game.Name}");
                    }
                }

                Debug.WriteLine($"[DataMigrationService] 扫描完成，共发现 {missingGames.Count} 个缺少.gmd文件的游戏");
                return missingGames;
            });
        }

        public async Task<MigrationDetail> MigrateGameToGmdAsync(Game game)
        {
            if (game == null)
                throw new ArgumentNullException(nameof(game));

            var detail = new MigrationDetail
            {
                GameId = game.Id,
                GameName = game.Name ?? string.Empty,
                Timestamp = DateTime.Now
            };

            try
            {
                if (!ValidateGameData(game))
                {
                    detail.Result = MigrationResult.Failed;
                    detail.Message = "游戏数据验证失败：缺少必要信息";
                    Debug.WriteLine($"[DataMigrationService] 迁移失败 - 数据验证失败: {game.Name}");
                    return detail;
                }

                var gmdFilePath = _gmdFileService.GetGmdFilePath(game.ExecutablePath, game.GameId);
                if (_gmdFileService.GmdFileExists(gmdFilePath))
                {
                    detail.Result = MigrationResult.AlreadyMigrated;
                    detail.Message = "游戏已存在.gmd文件";
                    Debug.WriteLine($"[DataMigrationService] 游戏已迁移: {game.Name}");
                    return detail;
                }

                Debug.WriteLine($"[DataMigrationService] 开始迁移游戏: {game.Name}");
                await _gmdFileService.SerializeGameToGmdAsync(game);

                detail.Result = MigrationResult.Success;
                detail.Message = "迁移成功";
                Debug.WriteLine($"[DataMigrationService] 游戏迁移成功: {game.Name}, .gmd路径: {gmdFilePath}");
            }
            catch (Exception ex)
            {
                detail.Result = MigrationResult.Failed;
                detail.Message = $"迁移异常: {ex.Message}";
                Debug.WriteLine($"[DataMigrationService] 游戏迁移失败: {game.Name}, 错误: {ex.Message}");
            }

            return detail;
        }

        public async Task<MigrationStatus> MigrateAllGamesAsync(IEnumerable<Game> games, IProgress<MigrationProgress>? progress = null)
        {
            if (games == null)
                throw new ArgumentNullException(nameof(games));

            var gamesList = games.ToList();
            var missingGmdGames = await ScanForMissingGmdFilesAsync(gamesList);

            Debug.WriteLine($"[DataMigrationService] 开始批量迁移，共 {missingGmdGames.Count} 个游戏需要迁移");

            var status = new MigrationStatus
            {
                TotalGames = gamesList.Count,
                PendingGames = missingGmdGames.Count
            };

            int currentIndex = 0;
            foreach (var game in missingGmdGames)
            {
                var progressInfo = new MigrationProgress
                {
                    CurrentGameIndex = currentIndex,
                    TotalGames = missingGmdGames.Count,
                    CurrentGameName = game.Name ?? string.Empty,
                    Percentage = missingGmdGames.Count > 0 ? (double)currentIndex / missingGmdGames.Count * 100 : 100,
                    Message = $"正在迁移: {game.Name}"
                };

                progress?.Report(progressInfo);

                var detail = await MigrateGameToGmdAsync(game);
                status.MigrationDetails.Add(detail);

                if (detail.Result == MigrationResult.Success)
                {
                    status.MigratedGames++;
                }
                else if (detail.Result == MigrationResult.Failed)
                {
                    status.FailedGames++;
                }

                currentIndex++;
            }

            status.PendingGames = missingGmdGames.Count - status.MigratedGames - status.FailedGames;

            var finalProgress = new MigrationProgress
            {
                CurrentGameIndex = currentIndex,
                TotalGames = missingGmdGames.Count,
                CurrentGameName = string.Empty,
                Percentage = 100,
                Message = $"迁移完成: 成功 {status.MigratedGames} 个, 失败 {status.FailedGames} 个"
            };

            progress?.Report(finalProgress);

            Debug.WriteLine($"[DataMigrationService] 批量迁移完成: 总计 {gamesList.Count} 个游戏, 成功 {status.MigratedGames} 个, 失败 {status.FailedGames} 个");

            return status;
        }

        public async Task<MigrationStatus> GetMigrationStatusAsync(IEnumerable<Game> games)
        {
            if (games == null)
                throw new ArgumentNullException(nameof(games));

            var gamesList = games.ToList();
            var status = new MigrationStatus
            {
                TotalGames = gamesList.Count
            };

            foreach (var game in gamesList)
            {
                if (game == null)
                    continue;

                var detail = new MigrationDetail
                {
                    GameId = game.Id,
                    GameName = game.Name ?? string.Empty,
                    Timestamp = DateTime.Now
                };

                if (string.IsNullOrWhiteSpace(game.ExecutablePath) || string.IsNullOrWhiteSpace(game.Name) || string.IsNullOrWhiteSpace(game.GameId))
                {
                    detail.Result = MigrationResult.Failed;
                    detail.Message = "游戏数据无效";
                    status.FailedGames++;
                }
                else
                {
                    var gmdFilePath = _gmdFileService.GetGmdFilePath(game.ExecutablePath, game.GameId);
                    if (_gmdFileService.GmdFileExists(gmdFilePath))
                    {
                        detail.Result = MigrationResult.Success;
                        detail.Message = "已迁移";
                        status.MigratedGames++;
                    }
                    else
                    {
                        detail.Result = MigrationResult.Pending;
                        detail.Message = "待迁移";
                        status.PendingGames++;
                    }
                }

                status.MigrationDetails.Add(detail);
            }

            Debug.WriteLine($"[DataMigrationService] 迁移状态查询: 总计 {status.TotalGames} 个游戏, 已迁移 {status.MigratedGames} 个, 待迁移 {status.PendingGames} 个, 失败 {status.FailedGames} 个");

            return await Task.FromResult(status);
        }

        private bool ValidateGameData(Game game)
        {
            if (string.IsNullOrWhiteSpace(game.ExecutablePath))
            {
                Debug.WriteLine($"[DataMigrationService] 验证失败: 游戏 {game.Name} 缺少可执行文件路径");
                return false;
            }

            if (string.IsNullOrWhiteSpace(game.Name))
            {
                Debug.WriteLine("[DataMigrationService] 验证失败: 游戏名称为空");
                return false;
            }

            if (string.IsNullOrWhiteSpace(game.GameId))
            {
                Debug.WriteLine($"[DataMigrationService] 验证失败: 游戏 {game.Name} 缺少GameId");
                return false;
            }

            if (!System.IO.File.Exists(game.ExecutablePath))
            {
                Debug.WriteLine($"[DataMigrationService] 验证失败: 游戏 {game.Name} 的可执行文件不存在: {game.ExecutablePath}");
                return false;
            }

            return true;
        }

        public async Task<int> AssignGameIdsToExistingGamesAsync()
        {
            var assignedCount = 0;

            using var connection = _dbContext.GetConnection();
            await connection.OpenAsync();

            using var selectCommand = connection.CreateCommand();
            selectCommand.CommandText = "SELECT Id FROM Games WHERE GameId IS NULL OR GameId = ''";

            var gameIds = new List<int>();
            using (var reader = await selectCommand.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    gameIds.Add(reader.GetInt32(0));
                }
            }

            foreach (var gameId in gameIds)
            {
                var gid = await GenerateUniqueGameIdForMigrationAsync(connection);

                using var updateCommand = connection.CreateCommand();
                updateCommand.CommandText = "UPDATE Games SET GameId = @GameId WHERE Id = @Id";
                updateCommand.Parameters.AddWithValue("@GameId", gid);
                updateCommand.Parameters.AddWithValue("@Id", gameId);
                await updateCommand.ExecuteNonQueryAsync();

                assignedCount++;
                Debug.WriteLine($"[DataMigrationService] 为游戏 ID={gameId} 分配了 GID: {gid}");
            }

            Debug.WriteLine($"[DataMigrationService] GID 分配完成，共 {assignedCount} 个游戏");
            return assignedCount;
        }

        private async Task<string> GenerateUniqueGameIdForMigrationAsync(Microsoft.Data.Sqlite.SqliteConnection connection)
        {
            const int maxRetries = 5;
            for (int attempt = 0; attempt < maxRetries; attempt++)
            {
                var digits = new byte[9];
                using (var rng = RandomNumberGenerator.Create())
                {
                    rng.GetBytes(digits);
                }

                var gid = "GID";
                for (int i = 0; i < 9; i++)
                {
                    gid += (digits[i] % 10).ToString();
                }

                using var checkCommand = connection.CreateCommand();
                checkCommand.CommandText = "SELECT COUNT(1) FROM Games WHERE GameId = @GameId";
                checkCommand.Parameters.AddWithValue("@GameId", gid);

                var result = await checkCommand.ExecuteScalarAsync();
                if (result == null || Convert.ToInt64(result) == 0)
                {
                    return gid;
                }
            }

            throw new InvalidOperationException($"无法在 {maxRetries} 次尝试内生成唯一的 GameId");
        }

        public async Task<MigrationStatus> MigrateGameImagesToGlobalDirectoryAsync(
            IEnumerable<Game> games, IProgress<MigrationProgress>? progress = null)
        {
            if (games == null)
                throw new ArgumentNullException(nameof(games));

            var gamesList = games.ToList();
            var imageService = new ImageService();
            var status = new MigrationStatus
            {
                TotalGames = gamesList.Count
            };

            Debug.WriteLine($"[DataMigrationService] 开始图片迁移，共 {gamesList.Count} 个游戏需要处理");

            int currentIndex = 0;
            foreach (var game in gamesList)
            {
                if (game == null || string.IsNullOrWhiteSpace(game.GameId))
                {
                    currentIndex++;
                    continue;
                }

                var progressInfo = new MigrationProgress
                {
                    CurrentGameIndex = currentIndex,
                    TotalGames = gamesList.Count,
                    CurrentGameName = game.Name ?? string.Empty,
                    Percentage = gamesList.Count > 0 ? (double)currentIndex / gamesList.Count * 100 : 100,
                    Message = $"正在迁移图片: {game.Name}"
                };

                progress?.Report(progressInfo);

                var detail = new MigrationDetail
                {
                    GameId = game.Id,
                    GameName = game.Name ?? string.Empty,
                    Timestamp = DateTime.Now
                };

                try
                {
                    bool needsUpdate = false;
                    bool iconPathUpdated = false;
                    List<string> newImagePaths = new List<string>();

                    // 迁移图标
                    if (!string.IsNullOrEmpty(game.IconPath) && System.IO.File.Exists(game.IconPath))
                    {
                        bool isInGlobalDir = game.IconPath.Contains("GameLauncher_Images");
                        if (!isInGlobalDir)
                        {
                            var newPath = await imageService.SaveIconAsync(game.GameId, game.IconPath);
                            if (!string.IsNullOrEmpty(newPath))
                            {
                                game.IconPath = newPath;
                                iconPathUpdated = true;
                                needsUpdate = true;
                            }
                        }
                    }

                    // 迁移预览图
                    var oldImagePaths = game.ImagePaths.ToList();
                    int previewIndex = 1;
                    foreach (var oldPath in oldImagePaths)
                    {
                        if (string.IsNullOrEmpty(oldPath) || !System.IO.File.Exists(oldPath))
                        {
                            previewIndex++;
                            continue;
                        }

                        bool isInGlobalDir = oldPath.Contains("GameLauncher_Images");
                        if (!isInGlobalDir)
                        {
                            var newPath = await imageService.SavePreviewImageAsync(game.GameId, oldPath, previewIndex);
                            if (!string.IsNullOrEmpty(newPath))
                            {
                                newImagePaths.Add(newPath);
                                needsUpdate = true;
                            }
                        }
                        else
                        {
                            newImagePaths.Add(oldPath);
                        }
                        previewIndex++;
                    }

                    // 更新数据库
                    if (needsUpdate)
                    {
                        using var connection = _dbContext.GetConnection();
                        await connection.OpenAsync();

                        if (iconPathUpdated)
                        {
                            using var cmd = connection.CreateCommand();
                            cmd.CommandText = "UPDATE Games SET IconPath = @IconPath WHERE Id = @Id";
                            cmd.Parameters.AddWithValue("@IconPath", game.IconPath);
                            cmd.Parameters.AddWithValue("@Id", game.Id);
                            await cmd.ExecuteNonQueryAsync();
                        }

                        // 更新 ImagePaths（JSON 格式）
                        var imagePathJson = System.Text.Json.JsonSerializer.Serialize(newImagePaths);
                        using var cmd2 = connection.CreateCommand();
                        cmd2.CommandText = "UPDATE Games SET ImagePaths = @ImagePaths WHERE Id = @Id";
                        cmd2.Parameters.AddWithValue("@ImagePaths", imagePathJson);
                        cmd2.Parameters.AddWithValue("@Id", game.Id);
                        await cmd2.ExecuteNonQueryAsync();

                        // 更新内存中的游戏对象
                        game.ImagePaths.Clear();
                        foreach (var path in newImagePaths)
                        {
                            game.ImagePaths.Add(path);
                        }
                    }

                    detail.Result = needsUpdate ? MigrationResult.Success : MigrationResult.AlreadyMigrated;
                    detail.Message = needsUpdate ? "图片迁移成功" : "图片已在全局目录";

                    if (needsUpdate)
                    {
                        status.MigratedGames++;
                    }
                    else
                    {
                        status.PendingGames++; // 已经迁移过的也算作 pending（不需要再迁移）
                    }
                }
                catch (Exception ex)
                {
                    detail.Result = MigrationResult.Failed;
                    detail.Message = $"图片迁移异常: {ex.Message}";
                    status.FailedGames++;
                    Debug.WriteLine($"[DataMigrationService] 游戏 {game.Name} 图片迁移失败: {ex.Message}");
                }

                status.MigrationDetails.Add(detail);
                currentIndex++;
            }

            var finalProgress = new MigrationProgress
            {
                CurrentGameIndex = currentIndex,
                TotalGames = gamesList.Count,
                CurrentGameName = string.Empty,
                Percentage = 100,
                Message = $"图片迁移完成: 成功 {status.MigratedGames} 个, 失败 {status.FailedGames} 个"
            };

            progress?.Report(finalProgress);

            Debug.WriteLine($"[DataMigrationService] 图片迁移完成: 总计 {gamesList.Count} 个游戏, 成功 {status.MigratedGames} 个, 失败 {status.FailedGames} 个");

            return status;
        }

        public async Task<int> CleanOldImageDirectoriesAsync(IEnumerable<Game> games)
        {
            if (games == null)
                throw new ArgumentNullException(nameof(games));

            int cleanedCount = 0;
            var processedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            await Task.Run(() =>
            {
                foreach (var game in games)
                {
                    if (game == null || string.IsNullOrWhiteSpace(game.ExecutablePath))
                        continue;

                    var directory = System.IO.Path.GetDirectoryName(game.ExecutablePath);
                    if (string.IsNullOrWhiteSpace(directory) || !System.IO.Directory.Exists(directory))
                        continue;

                    if (!processedDirectories.Add(directory))
                        continue;

                    try
                    {
                        // 清理旧的 GameLauncher_Resources 文件夹
                        var oldResourcePath = System.IO.Path.Combine(directory, "GameLauncher_Resources");
                        if (System.IO.Directory.Exists(oldResourcePath))
                        {
                            System.IO.Directory.Delete(oldResourcePath, recursive: true);
                            cleanedCount++;
                            Debug.WriteLine($"[DataMigrationService] 已清理旧图片目录: {oldResourcePath}");
                        }

                        // 清理旧的 GameLauncher_Images 文件夹（如果存在于游戏目录下）
                        var oldImagesPath = System.IO.Path.Combine(directory, "GameLauncher_Images");
                        if (System.IO.Directory.Exists(oldImagesPath))
                        {
                            System.IO.Directory.Delete(oldImagesPath, recursive: true);
                            cleanedCount++;
                            Debug.WriteLine($"[DataMigrationService] 已清理旧图片目录: {oldImagesPath}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[DataMigrationService] 清理目录失败: {directory}, 错误: {ex.Message}");
                    }
                }
            });

            Debug.WriteLine($"[DataMigrationService] 旧目录清理完成，共清理 {cleanedCount} 个目录");
            return cleanedCount;
        }
    }
}
