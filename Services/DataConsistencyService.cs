using GameLauncher.Data;
using GameLauncher.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace GameLauncher.Services
{
    public class ConsistencyConflictField
    {
        public string FieldName { get; set; } = string.Empty;
        public string DatabaseValue { get; set; } = string.Empty;
        public string GmdFileValue { get; set; } = string.Empty;
        public string ConflictType { get; set; } = string.Empty;
    }

    public class ConsistencyResult
    {
        public int GameId { get; set; }
        public string GameName { get; set; } = string.Empty;
        public bool IsConsistent { get; set; }
        public List<ConsistencyConflictField> ConflictFields { get; set; } = new List<ConsistencyConflictField>();
        public string RecommendedSource { get; set; } = string.Empty;
        public string Details { get; set; } = string.Empty;
    }

    public class ConsistencyReport
    {
        public int TotalGames { get; set; }
        public int ConsistentGames { get; set; }
        public int InconsistentGames { get; set; }
        public int MissingGmdGames { get; set; }
        public List<ConsistencyResult> Details { get; set; } = new List<ConsistencyResult>();
    }

    public class DataConsistencyService
    {
        private readonly GmdFileService _gmdFileService;
        private readonly DatabaseContext _dbContext;

        public DataConsistencyService(GmdFileService gmdFileService, DatabaseContext dbContext)
        {
            _gmdFileService = gmdFileService ?? throw new ArgumentNullException(nameof(gmdFileService));
            _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        }

        public async Task<ConsistencyResult> CheckConsistencyAsync(Game dbGame, string gmdFilePath)
        {
            if (dbGame == null)
                throw new ArgumentNullException(nameof(dbGame));
            if (string.IsNullOrWhiteSpace(gmdFilePath))
                throw new ArgumentException(".gmd文件路径不能为空", nameof(gmdFilePath));

            var result = new ConsistencyResult
            {
                GameId = dbGame.Id,
                GameName = dbGame.Name ?? string.Empty
            };

            Debug.WriteLine($"[DataConsistencyService] 开始检查游戏一致性: {dbGame.Name} (ID: {dbGame.Id})");

            if (!File.Exists(gmdFilePath))
            {
                result.IsConsistent = false;
                result.RecommendedSource = "Database";
                result.Details = ".gmd文件不存在";
                result.ConflictFields.Add(new ConsistencyConflictField
                {
                    FieldName = "GmdFile",
                    DatabaseValue = "存在",
                    GmdFileValue = "不存在",
                    ConflictType = "文件缺失"
                });
                Debug.WriteLine($"[DataConsistencyService] .gmd文件不存在: {gmdFilePath}");
                return result;
            }

            Game gmdGame;
            try
            {
                gmdGame = await _gmdFileService.DeserializeGameFromGmdAsync(gmdFilePath);
            }
            catch (Exception ex)
            {
                result.IsConsistent = false;
                result.RecommendedSource = "Database";
                result.Details = $".gmd文件解析失败: {ex.Message}";
                Debug.WriteLine($"[DataConsistencyService] .gmd文件解析失败: {ex.Message}");
                return result;
            }

            var conflicts = new List<ConsistencyConflictField>();

            CompareStringField(dbGame.Name, gmdGame.Name, "Name", conflicts);
            CompareStringField(dbGame.ExecutablePath, gmdGame.ExecutablePath, "ExecutablePath", conflicts);
            CompareStringField(dbGame.Description, gmdGame.Description, "Description", conflicts);
            CompareNumericField(dbGame.LaunchCount, gmdGame.LaunchCount, "LaunchCount", conflicts);
            CompareNumericField(dbGame.TotalPlayTime, gmdGame.TotalPlayTime, "TotalPlayTime", conflicts);
            CompareDateTimeField(dbGame.LastRunTime, gmdGame.LastRunTime, "LastRunTime", conflicts);
            CompareCollectionField(dbGame.Tags, gmdGame.Tags, "Tags", conflicts);
            CompareCollectionField(dbGame.ImagePaths, gmdGame.ImagePaths, "ImagePaths", conflicts);

            result.ConflictFields = conflicts;
            result.IsConsistent = conflicts.Count == 0;

            if (result.IsConsistent)
            {
                result.RecommendedSource = "Both";
                result.Details = "数据库与.gmd文件数据完全一致";
                Debug.WriteLine($"[DataConsistencyService] 游戏 {dbGame.Name} 数据一致");
            }
            else
            {
                result.RecommendedSource = DetermineRecommendedSource(dbGame, gmdGame);
                result.Details = $"发现 {conflicts.Count} 个字段不一致";
                Debug.WriteLine($"[DataConsistencyService] 游戏 {dbGame.Name} 发现 {conflicts.Count} 个冲突字段");
            }

            return result;
        }

        public async Task<Game> ResolveConflictAsync(Game dbGame, string gmdFilePath)
        {
            if (dbGame == null)
                throw new ArgumentNullException(nameof(dbGame));
            if (string.IsNullOrWhiteSpace(gmdFilePath))
                throw new ArgumentException(".gmd文件路径不能为空", nameof(gmdFilePath));

            Debug.WriteLine($"[DataConsistencyService] 开始解决游戏数据冲突: {dbGame.Name} (ID: {dbGame.Id})");

            if (!File.Exists(gmdFilePath))
            {
                Debug.WriteLine($"[DataConsistencyService] .gmd文件不存在，使用数据库数据: {gmdFilePath}");
                return CloneGame(dbGame);
            }

            Game gmdGame;
            try
            {
                gmdGame = await _gmdFileService.DeserializeGameFromGmdAsync(gmdFilePath);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DataConsistencyService] .gmd文件解析失败，使用数据库数据: {ex.Message}");
                return CloneGame(dbGame);
            }

            var resolvedGame = CloneGame(dbGame);

            var dbLastRunTime = dbGame.LastRunTime ?? DateTime.MinValue;
            var gmdLastRunTime = gmdGame.LastRunTime ?? DateTime.MinValue;

            resolvedGame.Name = gmdGame.Name ?? dbGame.Name;
            resolvedGame.Description = gmdGame.Description ?? dbGame.Description;

            resolvedGame.LaunchCount = Math.Max(dbGame.LaunchCount, gmdGame.LaunchCount);
            resolvedGame.TotalPlayTime = Math.Max(dbGame.TotalPlayTime, gmdGame.TotalPlayTime);

            if (gmdLastRunTime > dbLastRunTime)
            {
                resolvedGame.LastRunTime = gmdGame.LastRunTime;
            }
            else
            {
                resolvedGame.LastRunTime = dbGame.LastRunTime;
            }

            resolvedGame.Tags.Clear();
            var mergedTags = MergeCollections(dbGame.Tags, gmdGame.Tags);
            foreach (var tag in mergedTags)
            {
                resolvedGame.Tags.Add(tag);
            }

            resolvedGame.ImagePaths.Clear();
            var mergedImages = MergeCollections(dbGame.ImagePaths, gmdGame.ImagePaths);
            foreach (var image in mergedImages)
            {
                resolvedGame.ImagePaths.Add(image);
            }

            if (!string.IsNullOrEmpty(gmdGame.IconPath) && File.Exists(gmdGame.IconPath))
            {
                resolvedGame.IconPath = gmdGame.IconPath;
            }

            resolvedGame.GmdFilePath = gmdFilePath;
            resolvedGame.IsGmdFileReady = true;

            Debug.WriteLine($"[DataConsistencyService] 游戏 {dbGame.Name} 数据冲突解决完成");

            return resolvedGame;
        }

        public async Task<ConsistencyReport> CheckAllGamesConsistencyAsync(IEnumerable<Game> games)
        {
            if (games == null)
                throw new ArgumentNullException(nameof(games));

            var report = new ConsistencyReport();
            var gamesList = games.ToList();
            report.TotalGames = gamesList.Count;

            Debug.WriteLine($"[DataConsistencyService] 开始检查 {report.TotalGames} 个游戏的一致性");

            foreach (var game in gamesList)
            {
                var gmdFilePath = game.GmdFilePath;
                if (string.IsNullOrWhiteSpace(gmdFilePath))
                {
                    if (string.IsNullOrWhiteSpace(game.GameId))
                    {
                        var skipResult = new ConsistencyResult
                        {
                            GameId = game.Id,
                            GameName = game.Name,
                            IsConsistent = false,
                            RecommendedSource = "数据库",
                            Details = "缺少GameId，无法确定GMD文件路径"
                        };
                        skipResult.ConflictFields.Add(new ConsistencyConflictField
                        {
                            FieldName = "GmdFile",
                            DatabaseValue = "",
                            GmdFileValue = "文件路径无法确定（缺少GameId）",
                            ConflictType = "MissingGameId"
                        });
                        report.Details.Add(skipResult);
                        report.MissingGmdGames++;
                        continue;
                    }
                    gmdFilePath = _gmdFileService.GetGmdFilePath(game.ExecutablePath, game.GameId);
                }

                var result = await CheckConsistencyAsync(game, gmdFilePath);
                report.Details.Add(result);

                if (result.IsConsistent)
                {
                    report.ConsistentGames++;
                }
                else
                {
                    var hasMissingGmd = result.ConflictFields.Any(c => c.FieldName == "GmdFile");
                    if (hasMissingGmd)
                    {
                        report.MissingGmdGames++;
                    }
                    else
                    {
                        report.InconsistentGames++;
                    }
                }
            }

            Debug.WriteLine($"[DataConsistencyService] 一致性检查完成: 总计 {report.TotalGames}, 一致 {report.ConsistentGames}, 不一致 {report.InconsistentGames}, 缺少.gmd {report.MissingGmdGames}");

            return report;
        }

        public async Task<ConsistencyReport> CheckModifiedGamesConsistencyAsync(IEnumerable<Game> games)
        {
            var gamesList = games.ToList();
            var gamesToCheck = new List<Game>();

            using var connection = _dbContext.GetConnection();
            await connection.OpenAsync();

            var checkLog = new Dictionary<int, string>();
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT GameId, LastCheckTime FROM ConsistencyCheckLog";
                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    checkLog[reader.GetInt32(0)] = reader.GetString(1);
                }
            }

            foreach (var game in gamesList)
            {
                if (!checkLog.TryGetValue(game.Id, out var lastCheckStr))
                {
                    gamesToCheck.Add(game);
                }
                else
                {
                    if (DateTime.TryParse(lastCheckStr, out var lastCheck))
                    {
                        if (game.LastRunTime.HasValue && game.LastRunTime.Value > lastCheck)
                            gamesToCheck.Add(game);
                    }
                    else
                    {
                        gamesToCheck.Add(game);
                    }
                }
            }

            Debug.WriteLine($"[DataConsistencyService] 增量校验: {gamesToCheck.Count}/{gamesList.Count} 需要检查");

            var report = await CheckAllGamesConsistencyAsync(gamesToCheck);

            var now = DateTime.UtcNow.ToString("o");
            foreach (var game in gamesToCheck)
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = @"
                    INSERT OR REPLACE INTO ConsistencyCheckLog (GameId, LastCheckTime, WasConsistent)
                    VALUES (@GameId, @Time, @Consistent)";
                cmd.Parameters.AddWithValue("@GameId", game.Id);
                cmd.Parameters.AddWithValue("@Time", now);
                cmd.Parameters.AddWithValue("@Consistent",
                    report.Details.FirstOrDefault(d => d.GameId == game.Id)?.IsConsistent == true ? 1 : 0);
                await cmd.ExecuteNonQueryAsync();
            }

            return report;
        }

        private void CompareStringField(string dbValue, string gmdValue, string fieldName, List<ConsistencyConflictField> conflicts)
        {
            dbValue = dbValue ?? string.Empty;
            gmdValue = gmdValue ?? string.Empty;

            if (!string.Equals(dbValue, gmdValue, StringComparison.Ordinal))
            {
                conflicts.Add(new ConsistencyConflictField
                {
                    FieldName = fieldName,
                    DatabaseValue = string.IsNullOrEmpty(dbValue) ? "(空)" : dbValue,
                    GmdFileValue = string.IsNullOrEmpty(gmdValue) ? "(空)" : gmdValue,
                    ConflictType = "文本差异"
                });
            }
        }

        private void CompareNumericField(long dbValue, long gmdValue, string fieldName, List<ConsistencyConflictField> conflicts)
        {
            if (dbValue != gmdValue)
            {
                conflicts.Add(new ConsistencyConflictField
                {
                    FieldName = fieldName,
                    DatabaseValue = dbValue.ToString(),
                    GmdFileValue = gmdValue.ToString(),
                    ConflictType = "数值差异"
                });
            }
        }

        private void CompareDateTimeField(DateTime? dbValue, DateTime? gmdValue, string fieldName, List<ConsistencyConflictField> conflicts)
        {
            if (dbValue != gmdValue)
            {
                conflicts.Add(new ConsistencyConflictField
                {
                    FieldName = fieldName,
                    DatabaseValue = dbValue?.ToString("yyyy-MM-dd HH:mm:ss") ?? "(空)",
                    GmdFileValue = gmdValue?.ToString("yyyy-MM-dd HH:mm:ss") ?? "(空)",
                    ConflictType = "时间差异"
                });
            }
        }

        private void CompareCollectionField(System.Collections.ObjectModel.ObservableCollection<string> dbCollection,
            System.Collections.ObjectModel.ObservableCollection<string> gmdCollection,
            string fieldName, List<ConsistencyConflictField> conflicts)
        {
            var dbList = dbCollection?.ToList() ?? new List<string>();
            var gmdList = gmdCollection?.ToList() ?? new List<string>();

            var dbEmpty = dbList.Count == 0;
            var gmdEmpty = gmdList.Count == 0;

            if (dbEmpty && gmdEmpty)
                return;

            var dbSet = new HashSet<string>(dbList, StringComparer.Ordinal);
            var gmdSet = new HashSet<string>(gmdList, StringComparer.Ordinal);

            if (!dbSet.SetEquals(gmdSet))
            {
                var conflictType = (dbEmpty || gmdEmpty) ? "一方为空" : "集合差异";
                conflicts.Add(new ConsistencyConflictField
                {
                    FieldName = fieldName,
                    DatabaseValue = dbEmpty ? "(空)" : $"[{string.Join(", ", dbList)}]",
                    GmdFileValue = gmdEmpty ? "(空)" : $"[{string.Join(", ", gmdList)}]",
                    ConflictType = conflictType
                });
            }
        }

        private string DetermineRecommendedSource(Game dbGame, Game gmdGame)
        {
            var dbLastRunTime = dbGame.LastRunTime ?? DateTime.MinValue;
            var gmdLastRunTime = gmdGame.LastRunTime ?? DateTime.MinValue;

            if (gmdLastRunTime > dbLastRunTime)
            {
                return "GmdFile";
            }
            else if (dbLastRunTime > gmdLastRunTime)
            {
                return "Database";
            }
            else
            {
                return "Merge";
            }
        }

        private List<string> MergeCollections(System.Collections.ObjectModel.ObservableCollection<string> collection1,
            System.Collections.ObjectModel.ObservableCollection<string> collection2)
        {
            var list1 = collection1?.ToList() ?? new List<string>();
            var list2 = collection2?.ToList() ?? new List<string>();

            var merged = new List<string>();

            if (list1.Count == 0)
            {
                merged.AddRange(list2);
            }
            else if (list2.Count == 0)
            {
                merged.AddRange(list1);
            }
            else
            {
                var mergedSet = new HashSet<string>(list1, StringComparer.Ordinal);
                merged.AddRange(list1);

                foreach (var item in list2)
                {
                    if (!mergedSet.Contains(item))
                    {
                        merged.Add(item);
                        mergedSet.Add(item);
                    }
                }
            }

            return merged;
        }

        private Game CloneGame(Game source)
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));

            var clone = new Game
            {
                Id = source.Id,
                Name = source.Name ?? string.Empty,
                ExecutablePath = source.ExecutablePath ?? string.Empty,
                IconPath = source.IconPath ?? string.Empty,
                GmdFilePath = source.GmdFilePath ?? string.Empty,
                Description = source.Description ?? string.Empty,
                CreatedAt = source.CreatedAt,
                LaunchCount = source.LaunchCount,
                TotalPlayTime = source.TotalPlayTime,
                LastRunTime = source.LastRunTime,
                IsRunning = source.IsRunning,
                IsGmdFileReady = source.IsGmdFileReady
            };

            if (source.Tags != null)
            {
                foreach (var tag in source.Tags)
                {
                    clone.Tags.Add(tag);
                }
            }

            if (source.ImagePaths != null)
            {
                foreach (var path in source.ImagePaths)
                {
                    clone.ImagePaths.Add(path);
                }
            }

            return clone;
        }
    }
}
