using GameLauncher.Data;
using GameLauncher.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GameLauncher.Services
{
    /// <summary>
    /// 增量云同步服务：
    ///  - 支持本地同步文件夹 / Cloudflare R2 两种后端
    ///  - 手动立即同步、定时自动同步
    ///  - 维护最近 50 条同步历史
    ///  - R2 密钥使用 Windows DPAPI 加密后保存在本地 settings.json
    /// </summary>
    public sealed class IncrementalSyncService
    {
        private const int MaxHistoryCount = 50;
        private const string HistoryFileName = "history.json";
        private readonly DatabaseContext _dbContext;
        private readonly string _localRoot;
        private readonly string _syncDirectory;
        private readonly SemaphoreSlim _syncLock = new(1, 1);
        private readonly object _historyLock = new();
        private Timer? _timer;
        private bool _started;

        public event Action<SyncProgress>? ProgressChanged;
        public event Action<SyncResult>? SyncCompleted;

        public bool IsSyncing { get; private set; }
        public SyncResult? LastResult { get; private set; }
        public string LastBackendDisplayName { get; private set; } = string.Empty;

        public IncrementalSyncService(DatabaseContext dbContext)
        {
            _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
            _localRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GameLauncher");
            _syncDirectory = Path.Combine(_localRoot, ".sync");
        }

        public string LocalDataRoot => _localRoot;

        /// <summary>启动时调用：开启定时同步；启用状态下 30 秒后执行首次自动同步</summary>
        public void Start()
        {
            if (_started)
                return;
            _started = true;
            RestartTimer();
        }

        /// <summary>设置变更后调用，按最新间隔重建定时器</summary>
        public void RestartTimer()
        {
            _timer?.Dispose();
            _timer = null;

            var settings = UserSettings.Instance;
            if (!settings.CloudSyncEnabled || settings.CloudSyncIntervalMinutes <= 0)
                return;

            _timer = new Timer(
                _ => _ = RunScheduledSyncAsync(),
                null,
                TimeSpan.FromSeconds(30),
                TimeSpan.FromMinutes(settings.CloudSyncIntervalMinutes));
            Debug.WriteLine($"[IncrementalSync] 定时同步已启动，间隔 {settings.CloudSyncIntervalMinutes} 分钟");
        }

        private async Task RunScheduledSyncAsync()
        {
            if (IsSyncing)
                return;
            await SyncNowAsync();
        }

        /// <summary>
        /// 立即执行一次增量同步
        /// </summary>
        public async Task<SyncResult> SyncNowAsync(CancellationToken ct = default)
        {
            if (!await _syncLock.WaitAsync(0))
            {
                return new SyncResult
                {
                    Success = false,
                    ErrorMessage = "已有同步任务正在进行",
                    StartedUtc = DateTime.UtcNow,
                    FinishedUtc = DateTime.UtcNow
                };
            }

            IsSyncing = true;
            var result = new SyncResult { StartedUtc = DateTime.UtcNow };
            try
            {
                var settings = UserSettings.Instance;
                var backend = CreateBackend(settings, out var configError);
                if (backend == null)
                {
                    result.Success = false;
                    result.ErrorMessage = configError ?? "同步配置不完整";
                    result.FinishedUtc = DateTime.UtcNow;
                    return result;
                }

                LastBackendDisplayName = backend.DisplayName;
                await CheckpointDatabaseAsync();

                var direction = ParseDirection(settings.CloudSyncDirection);
                var progress = new Progress<SyncProgress>(p => ProgressChanged?.Invoke(p));
                var engine = new IncrementalSyncEngine(_localRoot, backend, BackendKey(settings), direction, progress);
                result = await engine.SyncAsync(ct);

                AppendHistory(result);
                var latest = UserSettings.Instance;
                latest.LastCloudSyncTime = DateTime.Now;
                latest.LastCloudSyncSummary = result.Summary;
                latest.Save();
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
                result.FinishedUtc = DateTime.UtcNow;
                Debug.WriteLine($"[IncrementalSync] 同步异常: {ex}");
            }
            finally
            {
                IsSyncing = false;
                LastResult = result;
                SyncCompleted?.Invoke(result);
                _syncLock.Release();
            }
            return result;
        }

        /// <summary>测试后端连接，成功返回 null</summary>
        public async Task<string?> TestConnectionAsync(CancellationToken ct = default)
        {
            var settings = UserSettings.Instance;
            var backend = CreateBackend(settings, out var configError);
            if (backend == null)
                return configError ?? "同步配置不完整";
            return await backend.TestAsync(ct);
        }

        public IReadOnlyList<SyncHistoryEntry> GetHistory()
        {
            lock (_historyLock)
            {
                return LoadHistory();
            }
        }

        private ISyncBackend? CreateBackend(UserSettings settings, out string? error)
        {
            error = null;
            try
            {
                if (string.Equals(settings.CloudSyncBackend, "CloudflareR2", StringComparison.OrdinalIgnoreCase))
                {
                    var secret = SecretProtector.Decrypt(settings.R2SecretAccessKey);
                    if (string.IsNullOrWhiteSpace(settings.R2Endpoint) ||
                        string.IsNullOrWhiteSpace(settings.R2Bucket) ||
                        string.IsNullOrWhiteSpace(settings.R2AccessKeyId) ||
                        string.IsNullOrWhiteSpace(secret))
                    {
                        error = "请完整填写 Cloudflare R2 配置（Endpoint、Bucket、Access Key ID、Secret Access Key）";
                        return null;
                    }
                    return new R2SyncBackend(settings.R2Endpoint, settings.R2Bucket, settings.R2AccessKeyId, secret);
                }

                if (string.IsNullOrWhiteSpace(settings.SyncFolderPath))
                {
                    error = "请选择同步文件夹";
                    return null;
                }

                var folderPath = Path.GetFullPath(settings.SyncFolderPath);
                if (!Directory.Exists(folderPath))
                {
                    error = $"同步文件夹不存在: {folderPath}";
                    return null;
                }
                if (SyncPath.IsInside(folderPath, _localRoot) ||
                    string.Equals(Path.GetFullPath(folderPath), Path.GetFullPath(_localRoot), StringComparison.OrdinalIgnoreCase))
                {
                    error = "同步文件夹不能是本地数据目录或它的子目录";
                    return null;
                }
                return new FolderSyncBackend(folderPath);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
        }

        private async Task CheckpointDatabaseAsync()
        {
            try
            {
                using var connection = _dbContext.GetConnection();
                await connection.OpenAsync();
                using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                await command.ExecuteNonQueryAsync();
                Debug.WriteLine("[IncrementalSync] SQLite WAL 检查点完成");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[IncrementalSync] WAL 检查点失败（继续同步）: {ex.Message}");
            }
        }

        private static string BackendKey(UserSettings settings)
        {
            return string.Equals(settings.CloudSyncBackend, "CloudflareR2", StringComparison.OrdinalIgnoreCase)
                ? "r2"
                : "folder";
        }

        public static SyncDirection ParseDirection(string? value)
        {
            return value switch
            {
                "UploadOnly" => SyncDirection.UploadOnly,
                "DownloadOnly" => SyncDirection.DownloadOnly,
                _ => SyncDirection.TwoWay
            };
        }

        private void AppendHistory(SyncResult result)
        {
            try
            {
                lock (_historyLock)
                {
                    var history = LoadHistory();
                    history.Insert(0, new SyncHistoryEntry
                    {
                        TimeUtc = DateTime.UtcNow,
                        Success = result.Success,
                        Backend = LastBackendDisplayName,
                        Direction = DirectionDisplayName(UserSettings.Instance.CloudSyncDirection),
                        Uploaded = result.UploadedCount,
                        Downloaded = result.DownloadedCount,
                        Deleted = result.DeletedLocalCount + result.DeletedRemoteCount,
                        Skipped = result.SkippedCount,
                        Conflicts = result.ConflictCount,
                        Failed = result.FailedCount,
                        Error = result.ErrorMessage
                    });
                    while (history.Count > MaxHistoryCount)
                        history.RemoveAt(history.Count - 1);
                    SaveHistory(history);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[IncrementalSync] 保存历史失败: {ex.Message}");
            }
        }

        private List<SyncHistoryEntry> LoadHistory()
        {
            try
            {
                var path = Path.Combine(_syncDirectory, HistoryFileName);
                if (File.Exists(path))
                {
                    var json = File.ReadAllText(path);
                    var file = JsonSerializer.Deserialize<SyncHistoryFile>(json);
                    if (file?.Entries != null)
                        return file.Entries;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[IncrementalSync] 读取历史失败: {ex.Message}");
            }
            return new List<SyncHistoryEntry>();
        }

        private void SaveHistory(List<SyncHistoryEntry> entries)
        {
            try
            {
                Directory.CreateDirectory(_syncDirectory);
                var json = JsonSerializer.Serialize(new SyncHistoryFile { Entries = entries }, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(Path.Combine(_syncDirectory, HistoryFileName), json);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[IncrementalSync] 写入历史失败: {ex.Message}");
            }
        }

        private static string DirectionDisplayName(string? value)
        {
            return value switch
            {
                "UploadOnly" => "仅上传",
                "DownloadOnly" => "仅下载",
                _ => "双向"
            };
        }
    }

    /// <summary>
    /// 使用 Windows DPAPI 对 R2 密钥做本机加密存储
    /// </summary>
    public static class SecretProtector
    {
        private const string Prefix = "DPAPI:";

        public static string Encrypt(string plainText)
        {
            if (string.IsNullOrEmpty(plainText))
                return string.Empty;
            var bytes = Encoding.UTF8.GetBytes(plainText);
            var protectedBytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            return Prefix + Convert.ToBase64String(protectedBytes);
        }

        public static string Decrypt(string storedValue)
        {
            if (string.IsNullOrEmpty(storedValue))
                return string.Empty;
            if (!storedValue.StartsWith(Prefix, StringComparison.Ordinal))
                return storedValue; // 兼容旧版明文
            try
            {
                var bytes = Convert.FromBase64String(storedValue.Substring(Prefix.Length));
                return Encoding.UTF8.GetString(ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SecretProtector] 解密失败: {ex.Message}");
                return string.Empty;
            }
        }
    }
}
