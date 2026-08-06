using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace GameLauncher.Services
{
    /// <summary>
    /// 本地同步文件夹后端（适用于 OneDrive/Dropbox/Syncthing 等已由其他工具同步的目录）
    /// 删除操作会把文件先移入远端目录下的 .trash，避免误删后无法恢复。
    /// </summary>
    public sealed class FolderSyncBackend : ISyncBackend
    {
        private const string TrashFolderName = ".trash";
        private readonly string _rootPath;

        public FolderSyncBackend(string rootPath)
        {
            _rootPath = Path.GetFullPath(rootPath);
            if (!Directory.Exists(_rootPath))
                throw new DirectoryNotFoundException($"同步文件夹不存在: {_rootPath}");
        }

        public string DisplayName => $"本地文件夹 {_rootPath}";
        public bool PreservesLocalTimestamp => true;

        public Task<IReadOnlyList<SyncFileEntry>> ListAsync(CancellationToken ct)
        {
            var entries = new List<SyncFileEntry>();
            foreach (var file in EnumerateFilesSafe(_rootPath))
            {
                ct.ThrowIfCancellationRequested();
                var rel = Path.GetRelativePath(_rootPath, file);
                if (rel.StartsWith(TrashFolderName + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    continue;
                try
                {
                    var info = new FileInfo(file);
                    entries.Add(new SyncFileEntry
                    {
                        RelativePath = SyncPath.Normalize(rel),
                        Size = info.Length,
                        TimestampUtc = info.LastWriteTimeUtc,
                        FileLastWriteUtc = info.LastWriteTimeUtc
                    });
                }
                catch
                {
                    // 文件被占用/删除时跳过
                }
            }
            return Task.FromResult<IReadOnlyList<SyncFileEntry>>(entries);
        }

        public async Task<SyncFileEntry?> GetEntryAsync(string relativePath, CancellationToken ct)
        {
            var fullPath = ToFullPath(relativePath);
            if (!File.Exists(fullPath))
                return null;
            var info = new FileInfo(fullPath);
            var sha = await SyncFileHash.ComputeSha256Async(fullPath, ct);
            return new SyncFileEntry
            {
                RelativePath = relativePath,
                Size = info.Length,
                TimestampUtc = info.LastWriteTimeUtc,
                FileLastWriteUtc = info.LastWriteTimeUtc,
                Sha256 = sha
            };
        }

        public Task<SyncUploadResult> UploadAsync(string relativePath, string localFilePath, string sha256, string? md5, CancellationToken ct)
        {
            var target = ToFullPath(relativePath);
            var targetDir = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(targetDir))
                Directory.CreateDirectory(targetDir);

            var tmp = target + $".glsync-tmp-{Guid.NewGuid():N}";
            try
            {
                File.Copy(localFilePath, tmp, overwrite: true);
                File.SetLastWriteTimeUtc(tmp, File.GetLastWriteTimeUtc(localFilePath));
                File.Move(tmp, target, overwrite: true);
            }
            catch
            {
                TryDelete(tmp);
                throw;
            }
            return Task.FromResult(new SyncUploadResult(null, File.GetLastWriteTimeUtc(target)));
        }

        public Task DownloadAsync(string relativePath, string destinationFilePath, CancellationToken ct)
        {
            var source = ToFullPath(relativePath);
            if (!File.Exists(source))
                throw new FileNotFoundException($"远端文件不存在: {relativePath}", source);

            var dir = Path.GetDirectoryName(destinationFilePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var tmp = destinationFilePath + $".glsync-tmp-{Guid.NewGuid():N}";
            try
            {
                File.Copy(source, tmp, overwrite: true);
                File.SetLastWriteTimeUtc(tmp, File.GetLastWriteTimeUtc(source));
                File.Move(tmp, destinationFilePath, overwrite: true);
            }
            catch
            {
                TryDelete(tmp);
                throw;
            }
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string relativePath, CancellationToken ct)
        {
            var target = ToFullPath(relativePath);
            if (!File.Exists(target))
                return Task.CompletedTask;

            var trashRoot = Path.Combine(_rootPath, TrashFolderName, DateTime.UtcNow.ToString("yyyyMMdd_HHmmss"));
            var trashPath = Path.Combine(trashRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            var trashDir = Path.GetDirectoryName(trashPath);
            if (!string.IsNullOrEmpty(trashDir))
                Directory.CreateDirectory(trashDir);
            File.Move(target, trashPath, overwrite: true);
            return Task.CompletedTask;
        }

        public async Task<string?> TestAsync(CancellationToken ct)
        {
            if (!Directory.Exists(_rootPath))
                return $"同步文件夹不存在: {_rootPath}";
            var probe = Path.Combine(_rootPath, $".glsync-write-test-{Guid.NewGuid():N}");
            try
            {
                await File.WriteAllTextAsync(probe, "ok", ct);
                File.Delete(probe);
                return null;
            }
            catch (Exception ex)
            {
                return $"文件夹不可写: {ex.Message}";
            }
        }

        private string ToFullPath(string relativePath)
        {
            var rel = SyncPath.Normalize(relativePath).Replace('/', Path.DirectorySeparatorChar);
            var full = Path.GetFullPath(Path.Combine(_rootPath, rel));
            var rootFull = Path.GetFullPath(_rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"非法远端路径: {relativePath}");
            return full;
        }

        private static IEnumerable<string> EnumerateFilesSafe(string root)
        {
            var pending = new Stack<string>();
            pending.Push(root);
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
                    var name = Path.GetFileName(sub);
                    if (string.Equals(name, TrashFolderName, StringComparison.OrdinalIgnoreCase))
                        continue;
                    pending.Push(sub);
                }
            }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
