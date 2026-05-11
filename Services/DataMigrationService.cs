using GameLauncher.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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

        public DataMigrationService(GmdFileService gmdFileService)
        {
            _gmdFileService = gmdFileService ?? throw new ArgumentNullException(nameof(gmdFileService));
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

                    if (string.IsNullOrWhiteSpace(game.ExecutablePath) || string.IsNullOrWhiteSpace(game.Name))
                    {
                        Debug.WriteLine($"[DataMigrationService] 跳过无效游戏数据: ID={game.Id}");
                        continue;
                    }

                    var gmdFilePath = _gmdFileService.GetGmdFilePath(game.ExecutablePath, game.Name);
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

                var gmdFilePath = _gmdFileService.GetGmdFilePath(game.ExecutablePath, game.Name);
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

                if (string.IsNullOrWhiteSpace(game.ExecutablePath) || string.IsNullOrWhiteSpace(game.Name))
                {
                    detail.Result = MigrationResult.Failed;
                    detail.Message = "游戏数据无效";
                    status.FailedGames++;
                }
                else
                {
                    var gmdFilePath = _gmdFileService.GetGmdFilePath(game.ExecutablePath, game.Name);
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

            if (!System.IO.File.Exists(game.ExecutablePath))
            {
                Debug.WriteLine($"[DataMigrationService] 验证失败: 游戏 {game.Name} 的可执行文件不存在: {game.ExecutablePath}");
                return false;
            }

            return true;
        }
    }
}
