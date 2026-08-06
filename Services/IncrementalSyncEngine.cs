using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

#pragma warning disable CS8602 // 此处为编译器的误报：AddSkip 的调用方已保证 local/remote/manifest 非空

namespace GameLauncher.Services
{
    /// <summary>
    /// 增量同步引擎：
    ///  1. 扫描本地数据目录与远端文件列表（仅取大小/时间戳，轻量）
    ///  2. 结合本地同步清单判断哪些文件真正变化（增量判定）
    ///  3. 按方向（双向/仅上传/仅下载）生成动作计划并执行
    ///  4. 冲突时以修改时间较新的一侧为准
    /// 删除安全：本地删除先进 .sync/trash，远端删除由后端先移入回收站。
    /// </summary>
    public sealed class IncrementalSyncEngine
    {
        private const string ManifestDirectory = ".sync";
        private const int MaxParallelism = 32;
        private static readonly TimeSpan TimestampTolerance = TimeSpan.FromSeconds(3);

        private readonly string _localRoot;
        private readonly ISyncBackend _backend;
        private readonly string _manifestPath;
        private readonly SyncDirection _direction;
        private readonly IProgress<SyncProgress>? _progress;
        private readonly object _manifestLock = new();

        private readonly ConcurrentDictionary<string, Task<(string Sha256, string Md5)>> _localHashCache = new(StringComparer.OrdinalIgnoreCase);
        public IncrementalSyncEngine(string localRoot, ISyncBackend backend, string backendKey, SyncDirection direction, IProgress<SyncProgress>? progress = null)
        {
            _localRoot = Path.GetFullPath(localRoot);
            _backend = backend ?? throw new ArgumentNullException(nameof(backend));
            _manifestPath = Path.Combine(_localRoot, ManifestDirectory, $"manifest.{backendKey}.json");
            _direction = direction;
            _progress = progress;
        }

        public async Task<SyncResult> SyncAsync(CancellationToken ct)
        {
            var result = new SyncResult();
            try
            {
                if (!Directory.Exists(_localRoot))
                {
                    result.Success = false;
                    result.ErrorMessage = $"本地数据目录不存在: {_localRoot}";
                    return result;
                }

                var localFiles = await ScanLocalAsync(ct);
                var remoteList = await _backend.ListAsync(ct);
                var remoteLookup = BuildRemoteLookup(remoteList);
                var manifest = await LoadManifestAsync(ct);

                var actions = new List<SyncAction>();
                await PlanAsync(localFiles, remoteLookup, manifest, actions, result, ct);
                result.TotalActions = actions.Count;
                result.Actions.AddRange(actions);

                await ExecuteAsync(actions, localFiles, remoteLookup, manifest, result, ct);
                await SaveManifestAsync(manifest, ct);

                result.Success = result.FailedCount == 0;
                if (!result.Success && string.IsNullOrEmpty(result.ErrorMessage))
                {
                    var firstFailed = result.Actions.FirstOrDefault(a => a.Error != null);
                    result.ErrorMessage = firstFailed != null
                        ? $"{result.FailedCount} 个文件同步失败（{firstFailed.RelativePath}: {firstFailed.Error}）"
                        : $"{result.FailedCount} 个文件同步失败";
                }
                result.FinishedUtc = DateTime.UtcNow;
            }
            catch (OperationCanceledException)
            {
                result.Success = false;
                result.ErrorMessage = "同步已取消";
                result.FinishedUtc = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
                result.FinishedUtc = DateTime.UtcNow;
                Debug.WriteLine($"[IncrementalSync] 同步异常: {ex}");
            }
            return result;
        }

        private Dictionary<string, SyncFileEntry> BuildRemoteLookup(IReadOnlyList<SyncFileEntry> remoteList)
        {
            var lookup = new Dictionary<string, SyncFileEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in remoteList)
            {
                lookup.TryAdd(entry.RelativePath, entry);
            }
            return lookup;
        }

        private Task<Dictionary<string, LocalFileInfo>> ScanLocalAsync(CancellationToken ct)
        {
            var files = new Dictionary<string, LocalFileInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in EnumerateLocalFilesSafe())
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var rel = Path.GetRelativePath(_localRoot, file).Replace('\\', '/');
                    if (ShouldExcludeLocal(rel))
                        continue;
                    var info = new FileInfo(file);
                    files[rel] = new LocalFileInfo
                    {
                        RelativePath = rel,
                        FullPath = file,
                        Size = info.Length,
                        LastWriteUtc = info.LastWriteTimeUtc
                    };
                }
                catch
                {
                    // 文件被占用/删除时跳过
                }
            }
            return Task.FromResult(files);
        }

        private IEnumerable<string> EnumerateLocalFilesSafe()
        {
            if (!Directory.Exists(_localRoot))
                yield break;
            var pending = new Stack<string>();
            pending.Push(_localRoot);
            while (pending.Count > 0)
            {
                var dir = pending.Pop();
                IEnumerable<string> subDirs;
                IEnumerable<string> files;
                try
                {
                    subDirs = Directory.EnumerateDirectories(dir);
                    files = Directory.EnumerateFiles(dir);
                }
                catch
                {
                    continue;
                }
                foreach (var file in files)
                    yield return file;
                foreach (var sub in subDirs)
                {
                    if (string.Equals(Path.GetFileName(sub), ManifestDirectory, StringComparison.OrdinalIgnoreCase))
                        continue;
                    pending.Push(sub);
                }
            }
        }

        private static bool ShouldExcludeLocal(string relativePath)
        {
            if (relativePath.StartsWith(ManifestDirectory + "/", StringComparison.OrdinalIgnoreCase))
                return true;
            // 设置文件包含本机同步配置与加密后的 R2 密钥，不做云同步
            if (string.Equals(relativePath, "settings.json", StringComparison.OrdinalIgnoreCase))
                return true;
            if (relativePath.EndsWith(".db-wal", StringComparison.OrdinalIgnoreCase) ||
                relativePath.EndsWith(".db-shm", StringComparison.OrdinalIgnoreCase))
                return true;
            if (relativePath.StartsWith("crash_log", StringComparison.OrdinalIgnoreCase))
                return true;
            return false;
        }

        private async Task PlanAsync(
            Dictionary<string, LocalFileInfo> localFiles,
            Dictionary<string, SyncFileEntry> remoteFiles,
            SyncManifest manifest,
            List<SyncAction> actions,
            SyncResult result,
            CancellationToken ct)
        {
            var allPaths = localFiles.Keys
                .Concat(remoteFiles.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase);

            foreach (var path in allPaths)
            {
                ct.ThrowIfCancellationRequested();
                localFiles.TryGetValue(path, out var local);
                remoteFiles.TryGetValue(path, out var remote);
                manifest.Entries.TryGetValue(path, out var entry);

                if (local != null && remote != null)
                {
                    await PlanBothExistAsync(path, local, remote, entry, manifest, actions, result, ct);
                }
                else if (local != null)
                {
                    PlanLocalOnly(path, local, entry, actions);
                }
                else if (remote != null)
                {
                    await PlanRemoteOnlyAsync(path, remote, entry, actions, ct);
                }
            }
        }

        private async Task PlanBothExistAsync(
            string path,
            LocalFileInfo local,
            SyncFileEntry remote,
            SyncManifestEntry? entry,
            SyncManifest manifest,
            List<SyncAction> actions,
            SyncResult result,
            CancellationToken ct)
        {
            if (entry == null)
            {
                // 首次同步：两侧都有
                var same = local.Size == remote.Size && TimestampsEqual(local.LastWriteUtc, remote.TimestampUtc);
                if (same)
                {
                    AddSkip(path, local, remote, manifest, actions);
                    return;
                }

                if (_direction == SyncDirection.UploadOnly)
                {
                    actions.Add(new SyncAction { RelativePath = path, Kind = SyncActionKind.Upload, Size = local.Size, Reason = "首次同步（仅上传）" });
                    return;
                }
                if (_direction == SyncDirection.DownloadOnly)
                {
                    actions.Add(new SyncAction { RelativePath = path, Kind = SyncActionKind.Download, Size = remote.Size, Reason = "首次同步（仅下载）" });
                    return;
                }

                var winner = ResolveFirstConflict(local, remote);
                result.ConflictCount++;
                actions.Add(new SyncAction
                {
                    RelativePath = path,
                    Kind = winner,
                    Size = winner == SyncActionKind.Upload ? local.Size : remote.Size,
                    Reason = "首次同步冲突，较新的一侧胜出"
                });
                return;
            }

            var localChanged = await IsLocalChangedAsync(local, entry, ct);
            var remoteChanged = await IsRemoteChangedAsync(remote, entry, path, ct);

            if (!localChanged && !remoteChanged)
            {
                AddSkip(path, local, remote, manifest, actions);
                return;
            }

            if (_direction == SyncDirection.UploadOnly)
            {
                actions.Add(new SyncAction { RelativePath = path, Kind = SyncActionKind.Upload, Size = local.Size, Reason = "本地有变更（仅上传）" });
                return;
            }
            if (_direction == SyncDirection.DownloadOnly)
            {
                actions.Add(new SyncAction { RelativePath = path, Kind = SyncActionKind.Download, Size = remote.Size, Reason = "远端有变更（仅下载）" });
                return;
            }

            if (localChanged && !remoteChanged)
            {
                actions.Add(new SyncAction { RelativePath = path, Kind = SyncActionKind.Upload, Size = local.Size, Reason = "本地变更" });
                return;
            }
            if (!localChanged && remoteChanged)
            {
                actions.Add(new SyncAction { RelativePath = path, Kind = SyncActionKind.Download, Size = remote.Size, Reason = "远端变更" });
                return;
            }

            // 两侧都变了：以较新的一侧为准
            var conflictWinner = await ResolveConflictAsync(local, remote, path, ct);
            result.ConflictCount++;
            actions.Add(new SyncAction
            {
                RelativePath = path,
                Kind = conflictWinner,
                Size = conflictWinner == SyncActionKind.Upload ? local.Size : remote.Size,
                Reason = "两侧同时变更，较新的一侧胜出"
            });
        }

        private void PlanLocalOnly(string path, LocalFileInfo local, SyncManifestEntry? entry, List<SyncAction> actions)
        {
            if (_direction == SyncDirection.DownloadOnly)
            {
                actions.Add(new SyncAction { RelativePath = path, Kind = SyncActionKind.Skip, Size = local.Size, Reason = "仅下载模式，保留本地文件" });
                return;
            }

            if (entry != null && _direction == SyncDirection.TwoWay)
            {
                // 之前两侧都有，现在远端缺失：远端被删除，双向同步中把删除传播到本地（先移入本地回收站）
                actions.Add(new SyncAction { RelativePath = path, Kind = SyncActionKind.DeleteLocal, Size = local.Size, Reason = "远端已删除" });
                return;
            }

            actions.Add(new SyncAction { RelativePath = path, Kind = SyncActionKind.Upload, Size = local.Size, Reason = "仅本地存在" });
        }

        private Task PlanRemoteOnlyAsync(string path, SyncFileEntry remote, SyncManifestEntry? entry, List<SyncAction> actions, CancellationToken ct)
        {
            if (_direction == SyncDirection.UploadOnly)
            {
                actions.Add(new SyncAction { RelativePath = path, Kind = SyncActionKind.Skip, Size = remote.Size, Reason = "仅上传模式，保留远端文件" });
                return Task.CompletedTask;
            }

            if (entry != null && _direction == SyncDirection.TwoWay)
            {
                // 之前两侧都有，现在本地缺失：本地被删除，双向同步中把删除传播到远端（后端先移入回收站）
                actions.Add(new SyncAction { RelativePath = path, Kind = SyncActionKind.DeleteRemote, Size = remote.Size, Reason = "本地已删除" });
                return Task.CompletedTask;
            }

            actions.Add(new SyncAction { RelativePath = path, Kind = SyncActionKind.Download, Size = remote.Size, Reason = "仅远端存在" });
            return Task.CompletedTask;
        }

        private async Task<bool> IsLocalChangedAsync(LocalFileInfo local, SyncManifestEntry entry, CancellationToken ct)
        {
            if (local.Size != entry.LocalSize || !TimestampsEqual(local.LastWriteUtc, entry.LocalLastWriteUtc))
                return true;
            if (string.IsNullOrEmpty(entry.LocalSha256))
                return false;
            var (sha, _) = await GetLocalHashesAsync(local, ct);
            return !string.Equals(sha, entry.LocalSha256, StringComparison.OrdinalIgnoreCase);
        }

        private Task<bool> IsRemoteChangedAsync(SyncFileEntry remote, SyncManifestEntry entry, string path, CancellationToken ct)
        {
            // R2 等后端列表自带 ETag：以 ETag 为内容身份，避免依赖服务器/客户端时钟
            if (!string.IsNullOrEmpty(entry.RemoteEtag) && !string.IsNullOrEmpty(remote.Etag))
                return Task.FromResult(!string.Equals(entry.RemoteEtag, remote.Etag, StringComparison.OrdinalIgnoreCase));

            if (remote.Size != entry.RemoteSize || !TimestampsEqual(remote.TimestampUtc, entry.RemoteIdentityUtc))
                return Task.FromResult(true);

            // 本地文件夹后端依赖 大小+修改时间 即可（文件系统会保留/更新 mtime）；
            // R2 后端通过列表 ETag 做内容校验，无需额外 HEAD。
            if (!_backend.PreservesLocalTimestamp && !string.IsNullOrEmpty(entry.RemoteSha256) && !string.IsNullOrEmpty(remote.Sha256))
                return Task.FromResult(!string.Equals(entry.RemoteSha256, remote.Sha256, StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(false);
        }

        private static SyncActionKind ResolveFirstConflict(LocalFileInfo local, SyncFileEntry remote)
        {
            // 首次合并时直接用列表自带的时间戳，避免每个文件额外 HEAD/SHA 请求
            return remote.TimestampUtc > local.LastWriteUtc.AddSeconds(1)
                ? SyncActionKind.Download
                : SyncActionKind.Upload;
        }

        private async Task<SyncActionKind> ResolveConflictAsync(LocalFileInfo local, SyncFileEntry remote, string path, CancellationToken ct)
        {
            var remoteFull = await _backend.GetEntryAsync(path, ct);
            var remoteTime = GetRemoteFileTime(remoteFull, remote);
            return remoteTime > local.LastWriteUtc.AddSeconds(1)
                ? SyncActionKind.Download
                : SyncActionKind.Upload;
        }

        private static DateTime GetRemoteFileTime(SyncFileEntry? remoteFull, SyncFileEntry remote)
        {
            if (remoteFull?.FileLastWriteUtc != null)
                return remoteFull.FileLastWriteUtc.Value;
            if (remoteFull?.TimestampUtc != default)
                return remoteFull.TimestampUtc;
            return remote.TimestampUtc;
        }

        private void AddSkip(string path, LocalFileInfo? local, SyncFileEntry? remote, SyncManifest manifest, List<SyncAction> actions)
        {
            var l = local ?? throw new InvalidOperationException("AddSkip 需要本地文件信息");
            var r = remote ?? throw new InvalidOperationException("AddSkip 需要远端文件信息");
            if (!manifest.Entries.ContainsKey(path))
            {
                SetManifestEntry(manifest, path, new SyncManifestEntry
                {
                    LocalSize = l.Size,
                    LocalLastWriteUtc = l.LastWriteUtc,
                    LocalSha256 = string.Empty,
                    RemoteSize = r.Size,
                    RemoteIdentityUtc = r.TimestampUtc,
                    RemoteEtag = r.Etag,
                    RemoteSha256 = null,
                    LastSyncedUtc = DateTime.UtcNow
                });
            }
            actions.Add(new SyncAction { RelativePath = path, Kind = SyncActionKind.Skip, Size = l.Size, Reason = "两侧一致" });
        }

        private async Task ExecuteAsync(
            List<SyncAction> actions,
            Dictionary<string, LocalFileInfo> localFiles,
            Dictionary<string, SyncFileEntry> remoteFiles,
            SyncManifest manifest,
            SyncResult result,
            CancellationToken ct)
        {
            if (actions.Count == 0)
                return;

            var queue = new ConcurrentQueue<SyncAction>(actions);
            var workers = Enumerable.Range(0, MaxParallelism)
                .Select(_ => Task.Run(() => WorkerAsync(queue, localFiles, remoteFiles, manifest, result, ct), ct))
                .ToArray();
            await Task.WhenAll(workers);
        }

        private async Task WorkerAsync(
            ConcurrentQueue<SyncAction> queue,
            Dictionary<string, LocalFileInfo> localFiles,
            Dictionary<string, SyncFileEntry> remoteFiles,
            SyncManifest manifest,
            SyncResult result,
            CancellationToken ct)
        {
            while (queue.TryDequeue(out var action))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    switch (action.Kind)
                    {
                        case SyncActionKind.Upload:
                            await DoUploadAsync(action, localFiles, manifest, result, ct);
                            break;
                        case SyncActionKind.Download:
                            await DoDownloadAsync(action, remoteFiles, manifest, result, ct);
                            break;
                        case SyncActionKind.DeleteLocal:
                            await DoDeleteLocalAsync(action, localFiles, manifest, result, ct);
                            break;
                        case SyncActionKind.DeleteRemote:
                            await DoDeleteRemoteAsync(action, manifest, result, ct);
                            break;
                        case SyncActionKind.Skip:
                            Interlocked.Increment(ref result.SkippedCount);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    action.Error = ex.Message;
                    Interlocked.Increment(ref result.FailedCount);
                    Debug.WriteLine($"[IncrementalSync] 动作失败 {action.Kind} {action.RelativePath}: {ex.Message}");
                }

                var processed = Interlocked.Increment(ref result.ProcessedActions);
                var transferred = Interlocked.Read(ref result.UploadedBytes) + Interlocked.Read(ref result.DownloadedBytes);
                _progress?.Report(new SyncProgress
                {
                    CurrentFile = action.RelativePath,
                    CompletedActions = processed,
                    TotalActions = result.TotalActions,
                    TransferredBytes = transferred
                });
            }
        }

        private async Task DoUploadAsync(
            SyncAction action,
            Dictionary<string, LocalFileInfo> localFiles,
            SyncManifest manifest,
            SyncResult result,
            CancellationToken ct)
        {
            if (!localFiles.TryGetValue(action.RelativePath, out var local) || !File.Exists(local.FullPath))
            {
                throw new FileNotFoundException($"本地文件已不存在: {action.RelativePath}");
            }

            // 数据库等文件可能被本应用（SQLite 连接池）占用，直接让 SDK 打开原文件会因
            // FileShare 冲突失败；先复制到 .sync/tmp 再从副本上传，并带重试
            var tmpDir = Path.Combine(_localRoot, ManifestDirectory, "tmp");
            Directory.CreateDirectory(tmpDir);
            var tmpPath = Path.Combine(tmpDir, $"upload_{Guid.NewGuid():N}");
            string sha;
            string md5;
            SyncUploadResult upload;
            try
            {
                await CopyFileWithRetryAsync(local.FullPath, tmpPath, ct);
                (sha, md5) = await SyncFileHash.ComputeSha256AndMd5Async(tmpPath, ct);
                _localHashCache[local.RelativePath] = Task.FromResult((sha, md5));
                upload = await _backend.UploadAsync(action.RelativePath, tmpPath, sha, md5, ct);
            }
            finally
            {
                TryDelete(tmpPath);
            }

            SetManifestEntry(manifest, action.RelativePath, new SyncManifestEntry
            {
                LocalSha256 = sha,
                LocalSize = local.Size,
                LocalLastWriteUtc = local.LastWriteUtc,
                RemoteSha256 = _backend.PreservesLocalTimestamp ? sha : null,
                RemoteEtag = upload.Etag,
                RemoteSize = local.Size,
                RemoteIdentityUtc = upload.LastModifiedUtc ??
                    (_backend.PreservesLocalTimestamp ? local.LastWriteUtc : DateTime.UtcNow),
                LastSyncedUtc = DateTime.UtcNow
            });
            Interlocked.Increment(ref result.UploadedCount);
            Interlocked.Add(ref result.UploadedBytes, local.Size);
        }

        private static async Task CopyFileWithRetryAsync(string sourcePath, string destinationPath, CancellationToken ct)
        {
            const int maxAttempts = 4;
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var destination = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
                    await source.CopyToAsync(destination, ct);
                    return;
                }
                catch (IOException) when (attempt < maxAttempts)
                {
                    Debug.WriteLine($"[IncrementalSync] 复制文件被占用，重试 {attempt}/3: {sourcePath}");
                    await Task.Delay(TimeSpan.FromMilliseconds(400 * attempt), ct);
                }
            }
        }

        private async Task DoDownloadAsync(
            SyncAction action,
            Dictionary<string, SyncFileEntry> remoteFiles,
            SyncManifest manifest,
            SyncResult result,
            CancellationToken ct)
        {
            if (!remoteFiles.TryGetValue(action.RelativePath, out var remote))
                throw new FileNotFoundException($"远端文件已不存在: {action.RelativePath}");

            var destPath = Path.Combine(_localRoot, action.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            var destDir = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(destDir))
                Directory.CreateDirectory(destDir);

            var tmpDir = Path.Combine(_localRoot, ManifestDirectory, "tmp");
            Directory.CreateDirectory(tmpDir);
            var tmpPath = Path.Combine(tmpDir, Guid.NewGuid().ToString("N"));

            try
            {
                await _backend.DownloadAsync(action.RelativePath, tmpPath, ct);
                if (!File.Exists(tmpPath))
                    throw new InvalidOperationException($"下载未产生文件: {action.RelativePath}");
                File.Move(tmpPath, destPath, overwrite: true);
            }
            catch
            {
                TryDelete(tmpPath);
                throw;
            }

            var info = new FileInfo(destPath);
            var sha = await SyncFileHash.ComputeSha256Async(destPath, ct);
            SetManifestEntry(manifest, action.RelativePath, new SyncManifestEntry
            {
                LocalSha256 = sha,
                LocalSize = info.Length,
                LocalLastWriteUtc = info.LastWriteTimeUtc,
                RemoteSha256 = _backend.PreservesLocalTimestamp ? sha : null,
                RemoteEtag = remote.Etag,
                RemoteSize = remote.Size,
                RemoteIdentityUtc = remote.TimestampUtc,
                LastSyncedUtc = DateTime.UtcNow
            });
            Interlocked.Increment(ref result.DownloadedCount);
            Interlocked.Add(ref result.DownloadedBytes, info.Length);
        }

        private Task DoDeleteLocalAsync(
            SyncAction action,
            Dictionary<string, LocalFileInfo> localFiles,
            SyncManifest manifest,
            SyncResult result,
            CancellationToken ct)
        {
            var destPath = Path.Combine(_localRoot, action.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(destPath))
            {
                var trashRoot = Path.Combine(_localRoot, ManifestDirectory, "trash", DateTime.UtcNow.ToString("yyyyMMdd_HHmmss"));
                var trashPath = Path.Combine(trashRoot, action.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                var trashDir = Path.GetDirectoryName(trashPath);
                if (!string.IsNullOrEmpty(trashDir))
                    Directory.CreateDirectory(trashDir);
                File.Move(destPath, trashPath, overwrite: true);
            }
            RemoveManifestEntry(manifest, action.RelativePath);
            Interlocked.Increment(ref result.DeletedLocalCount);
            return Task.CompletedTask;
        }

        private async Task DoDeleteRemoteAsync(SyncAction action, SyncManifest manifest, SyncResult result, CancellationToken ct)
        {
            await _backend.DeleteAsync(action.RelativePath, ct);
            RemoveManifestEntry(manifest, action.RelativePath);
            Interlocked.Increment(ref result.DeletedRemoteCount);
        }

        private async Task<(string Sha256, string Md5)> GetLocalHashesAsync(LocalFileInfo local, CancellationToken ct)
        {
            return await _localHashCache.GetOrAdd(
                local.RelativePath,
                _ => SyncFileHash.ComputeSha256AndMd5Async(local.FullPath, ct));
        }

        private static bool TimestampsEqual(DateTime a, DateTime b)
        {
            return Math.Abs((a - b).TotalSeconds) < TimestampTolerance.TotalSeconds;
        }

        private async Task<SyncManifest> LoadManifestAsync(CancellationToken ct)
        {
            try
            {
                if (File.Exists(_manifestPath))
                {
                    var json = await File.ReadAllTextAsync(_manifestPath, ct);
                    var manifest = JsonSerializer.Deserialize<SyncManifest>(json);
                    if (manifest != null)
                    {
                        manifest.Entries ??= new Dictionary<string, SyncManifestEntry>(StringComparer.OrdinalIgnoreCase);
                        if (!ReferenceEquals(manifest.Entries.Comparer, StringComparer.OrdinalIgnoreCase))
                        {
                            manifest.Entries = new Dictionary<string, SyncManifestEntry>(manifest.Entries, StringComparer.OrdinalIgnoreCase);
                        }
                        return manifest;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[IncrementalSync] 读取同步清单失败，重建清单: {ex.Message}");
            }
            return new SyncManifest();
        }

        private async Task SaveManifestAsync(SyncManifest manifest, CancellationToken ct)
        {
            var dir = Path.GetDirectoryName(_manifestPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            lock (_manifestLock)
            {
                manifest.UpdatedAt = DateTime.UtcNow;
            }
            var tmp = _manifestPath + $".tmp-{Guid.NewGuid():N}";
            var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(tmp, json, ct);
            File.Move(tmp, _manifestPath, overwrite: true);
        }

        private void SetManifestEntry(SyncManifest manifest, string path, SyncManifestEntry entry)
        {
            lock (_manifestLock)
            {
                manifest.Entries[path] = entry;
            }
        }

        private void RemoveManifestEntry(SyncManifest manifest, string path)
        {
            lock (_manifestLock)
            {
                manifest.Entries.Remove(path);
            }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
