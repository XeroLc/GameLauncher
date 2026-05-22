using GameLauncher.Data;
using GameLauncher.Models;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace GameLauncher.Services
{
    /// <summary>
    /// 数据同步变更类型
    /// </summary>
    public enum ChangeType
    {
        None,
        Added,
        Modified,
        Deleted
    }

    /// <summary>
    /// 数据变更结果
    /// </summary>
    public class ChangeResult
    {
        public ChangeType Type { get; set; }
        public Game? Game { get; set; }
        public int GameId { get; set; }
        public List<string> ChangedFields { get; set; } = new();
    }

    /// <summary>
    /// 同步结果摘要
    /// </summary>
    public class SyncSummary
    {
        public bool HasChanges { get; set; }
        public int AddedCount { get; set; }
        public int ModifiedCount { get; set; }
        public int DeletedCount { get; set; }
        public List<ChangeResult> Changes { get; set; } = new();
        public string Description { get; set; } = string.Empty;
        public DateTime SyncTime { get; set; } = DateTime.UtcNow;
        public long ElapsedMs { get; set; }
    }

    /// <summary>
    /// 数据同步服务 — 提供静默刷新机制
    /// 核心特性：
    ///   1. 基于数据签名对比检测变更
    ///   2. O(n) 时间复杂度对比算法
    ///   3. 仅在有实质性变化时更新
    ///   4. 支持字段级别对比和自定义规则
    ///   5. 更新过程透明无闪烁
    ///   6. 异常优雅降级
    ///   7. 提供更新日志记录
    /// </summary>
    public class DataSyncService
    {
        private readonly DatabaseContext _dbContext;

        /// <summary>
        /// 更新日志列表
        /// </summary>
        public List<SyncSummary> SyncHistory { get; } = new();
        private const int MaxSyncHistoryCount = 50;

        private readonly List<Func<Game, Game, List<string>>> _customComparers = new();

        public DataSyncService(DatabaseContext dbContext)
        {
            _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        }

        /// <summary>
        /// 注册自定义字段对比规则
        /// </summary>
        public void RegisterCustomComparer(Func<Game, Game, List<string>> comparer)
        {
            _customComparers.Add(comparer);
        }

        /// <summary>
        /// 计算游戏数据签名（用于快速判断整体是否变化）
        /// 使用 COUNT + MAX(LastRunTime) 轻量数据库查询，不再全量遍历内存中的游戏对象
        /// </summary>
        public async Task<string> ComputeDataSignatureAsync()
        {
            using var connection = _dbContext.GetConnection();
            await connection.OpenAsync();

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) as Count, MAX(LastRunTime) as MaxUpdatedAt FROM Games";

            using var reader = await command.ExecuteReaderAsync();
            await reader.ReadAsync();

            var count = reader.GetInt32(0);
            var maxUpdatedAtStr = reader.IsDBNull(1) ? "" : reader.GetDateTime(1).ToString("o");

            var signatureString = $"{count}|{maxUpdatedAtStr}";
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(signatureString));
            return Convert.ToBase64String(bytes);
        }

        /// <summary>
        /// 对比内存数据与数据库最新数据，返回变更结果
        /// 时间复杂度：O(n)，n 为游戏数量
        /// </summary>
        public List<ChangeResult> DetectChanges(IEnumerable<Game> existingGames, IEnumerable<Game> latestGames)
        {
            var changes = new List<ChangeResult>();

            var existingDict = existingGames.ToDictionary(g => g.Id, g => g);
            var latestDict = latestGames.ToDictionary(g => g.Id, g => g);

            // 检测新增和修改
            foreach (var latest in latestGames)
            {
                if (!existingDict.TryGetValue(latest.Id, out var existing))
                {
                    changes.Add(new ChangeResult
                    {
                        Type = ChangeType.Added,
                        Game = latest,
                        GameId = latest.Id
                    });
                }
                else
                {
                    var changedFields = CompareFields(existing, latest);
                    if (changedFields.Count > 0)
                    {
                        changes.Add(new ChangeResult
                        {
                            Type = ChangeType.Modified,
                            Game = latest,
                            GameId = latest.Id,
                            ChangedFields = changedFields
                        });
                    }
                }
            }

            // 检测删除
            foreach (var existing in existingGames)
            {
                if (!latestDict.ContainsKey(existing.Id))
                {
                    changes.Add(new ChangeResult
                    {
                        Type = ChangeType.Deleted,
                        GameId = existing.Id
                    });
                }
            }

            return changes;
        }

        /// <summary>
        /// 对比两个游戏对象的字段差异
        /// </summary>
        private List<string> CompareFields(Game existing, Game latest)
        {
            var changedFields = new List<string>();

            if (NormalizeString(existing.Name) != NormalizeString(latest.Name))
                changedFields.Add(nameof(Game.Name));

            if (NormalizeString(existing.ExecutablePath) != NormalizeString(latest.ExecutablePath))
                changedFields.Add(nameof(Game.ExecutablePath));

            if (NormalizeString(existing.Description) != NormalizeString(latest.Description))
                changedFields.Add(nameof(Game.Description));

            if (existing.LaunchCount != latest.LaunchCount)
                changedFields.Add(nameof(Game.LaunchCount));

            if (existing.TotalPlayTime != latest.TotalPlayTime)
                changedFields.Add(nameof(Game.TotalPlayTime));

            if (existing.LastRunTime != latest.LastRunTime)
                changedFields.Add(nameof(Game.LastRunTime));

            if (NormalizeString(existing.IconPath) != NormalizeString(latest.IconPath))
                changedFields.Add(nameof(Game.IconPath));

            if (NormalizeString(existing.GmdFilePath) != NormalizeString(latest.GmdFilePath))
                changedFields.Add(nameof(Game.GmdFilePath));

            if (existing.IsGmdFileReady != latest.IsGmdFileReady)
                changedFields.Add(nameof(Game.IsGmdFileReady));

            if (!AreCollectionsEqual(existing.Tags, latest.Tags))
                changedFields.Add(nameof(Game.Tags));

            if (!AreCollectionsEqual(existing.ImagePaths, latest.ImagePaths))
                changedFields.Add(nameof(Game.ImagePaths));

            if (!AreGameCollectionsEqual(existing.Collections, latest.Collections))
                changedFields.Add(nameof(Game.Collections));

            foreach (var comparer in _customComparers)
            {
                var customChanges = comparer(existing, latest);
                if (customChanges != null)
                {
                    changedFields.AddRange(customChanges);
                }
            }

            return changedFields;
        }

        private static string NormalizeString(string? value)
        {
            return string.IsNullOrEmpty(value) ? string.Empty : value;
        }

        /// <summary>
        /// 对比两个字符串集合是否相等
        /// </summary>
        private bool AreCollectionsEqual(IEnumerable<string> collection1, IEnumerable<string> collection2)
        {
            var list1 = collection1.ToList();
            var list2 = collection2.ToList();

            if (list1.Count != list2.Count)
                return false;

            for (int i = 0; i < list1.Count; i++)
            {
                if (list1[i] != list2[i])
                    return false;
            }

            return true;
        }

        private bool AreGameCollectionsEqual(IEnumerable<GameCollection>? c1, IEnumerable<GameCollection>? c2)
        {
            if (c1 == null && c2 == null) return true;
            if (c1 == null || c2 == null) return false;
            var ids1 = c1.Select(c => c.Id).OrderBy(id => id);
            var ids2 = c2.Select(c => c.Id).OrderBy(id => id);
            return ids1.SequenceEqual(ids2);
        }

        /// <summary>
        /// 执行静默同步
        /// 返回同步结果摘要，供调用方根据需要进行 UI 更新
        /// </summary>
        public async Task<SyncSummary> SyncAsync(
            IEnumerable<Game> existingGames,
            Func<Task<IEnumerable<Game>>> fetchLatestGames,
            Action<Game> applyAdd,
            Action<Game, IEnumerable<string>> applyModify,
            Action<int> applyDelete,
            bool forceRefresh = false)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var summary = new SyncSummary();

            try
            {
                var existingList = existingGames.ToList();

                // 如果强制刷新，直接重新加载
                if (forceRefresh)
                {
                    var allLatestGames = await fetchLatestGames();
                    summary.HasChanges = true;
                    summary.Description = "强制刷新";
                    sw.Stop();
                    summary.ElapsedMs = sw.ElapsedMilliseconds;
                    summary.SyncTime = DateTime.UtcNow;
                    SyncHistory.Add(summary);
                    TrimSyncHistory();
                    return summary;
                }

                var latestGames = await fetchLatestGames();

                var changes = DetectChanges(existingList, latestGames);

                if (changes.Count == 0)
                {
                    sw.Stop();
                    summary.HasChanges = false;
                    summary.Description = "无变化";
                    summary.ElapsedMs = sw.ElapsedMilliseconds;
                    summary.SyncTime = DateTime.UtcNow;
                    SyncHistory.Add(summary);
                    TrimSyncHistory();
                    System.Diagnostics.Debug.WriteLine($"[DataSync] 静默跳过 — 无变更，耗时 {sw.ElapsedMilliseconds}ms");
                    return summary;
                }

                foreach (var change in changes)
                {
                    try
                    {
                        switch (change.Type)
                        {
                            case ChangeType.Added:
                                applyAdd(change.Game!);
                                summary.AddedCount++;
                                break;

                            case ChangeType.Modified:
                                applyModify(change.Game!, change.ChangedFields);
                                summary.ModifiedCount++;
                                break;

                            case ChangeType.Deleted:
                                applyDelete(change.GameId);
                                summary.DeletedCount++;
                                break;
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[DataSync] 应用变更失败: {change.Type} GameId={change.GameId}, {ex.Message}");
                    }
                }

                summary.HasChanges = changes.Count > 0;
                summary.Changes = changes;
                summary.Description = $"新增 {summary.AddedCount} / 修改 {summary.ModifiedCount} / 删除 {summary.DeletedCount}";
            }
            catch (Exception ex)
            {
                summary.HasChanges = false;
                summary.Description = $"同步异常: {ex.Message}";
                System.Diagnostics.Debug.WriteLine($"[DataSync] 同步异常: {ex.Message}");
            }
            finally
            {
                sw.Stop();
                summary.ElapsedMs = sw.ElapsedMilliseconds;
                summary.SyncTime = DateTime.UtcNow;
                SyncHistory.Add(summary);
                TrimSyncHistory();
            }

            return summary;
        }

        private void TrimSyncHistory()
        {
            while (SyncHistory.Count > MaxSyncHistoryCount)
            {
                SyncHistory.RemoveAt(0);
            }
        }
    }
}
