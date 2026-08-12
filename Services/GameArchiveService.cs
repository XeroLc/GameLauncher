using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GameLauncher.Data;
using GameLauncher.Models;

namespace GameLauncher.Services
{
    /// <summary>归档/下载进度</summary>
    public class ArchiveProgress
    {
        public enum TransferStage { Packaging, Uploading, Downloading, Done }

        public TransferStage Stage { get; set; }
        public int PartIndex { get; set; }          // 当前分卷（从 1 开始）
        public int PartCount { get; set; }
        public double OverallPercent { get; set; }  // 0-100
        public string CurrentFile { get; set; } = string.Empty;
    }

    /// <summary>
    /// 游戏云归档服务：将游戏打包为 .vault 分卷（zip 结构 + 魔数头）上传 123 云盘，
    /// 或从云盘下载分卷并解压恢复到游戏目录。
    /// </summary>
    public class GameArchiveService
    {
        public const string VaultExtension = ".vault";
        public const string VaultMetadataEntry = "__vault.json";
        private const long VaultMaxPartSize = 5L * 1024 * 1024 * 1024; // 5GB
        private const string VaultFolderName = "GameLauncherVault";
        private const int SliceConcurrency = 4;

        // 魔数 "GLVLT"（GameLauncher Vault），文件头固定 20 字节：
        // [5B 魔数][2B 版本][8B 原始总大小][4B 本卷序号][1B 总卷数]
        private static readonly byte[] VaultSignature = { 0x47, 0x4C, 0x56, 0x4C, 0x54 };
        private const int VaultFormatVersion = 1;
        private const int VaultHeaderSize = 20;

        private static readonly SemaphoreSlim _transferLock = new(1, 1);

        private static void Log(string message)
        {
            try
            {
                var logPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "GameLauncher", "archive_log.txt");
                File.AppendAllText(logPath, $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
            }
            catch { }
        }

        private readonly Pan123Client _pan123;
        private readonly GameRepository _repository;
        private readonly GmdFileService _gmdService;
        private readonly ImageService _imageService;
        private readonly UserSettings _settings;
        private string _currentGameId = string.Empty;

        public GameArchiveService(Pan123Client pan123, GameRepository repository, GmdFileService gmdService, ImageService imageService)
        {
            _pan123 = pan123;
            _repository = repository;
            _gmdService = gmdService;
            _imageService = imageService;
            _settings = UserSettings.Instance;
        }

        /// <summary>当前是否有归档/下载任务在执行（全局互斥）</summary>
        public bool IsTransferActive => _transferLock.CurrentCount == 0;

        // ---------- 归档上传 ----------

        public async Task ArchiveGameAsync(Game game, IProgress<ArchiveProgress>? progress = null, CancellationToken ct = default)
        {
            await _transferLock.WaitAsync(ct);
            try
            {
                try
                {
                    await ArchiveGameCoreAsync(game, progress, ct);
                }
                catch (Exception ex)
                {
                    Log($"归档失败: {ex.GetType().Name}: {ex.Message}");
                    if (ex.InnerException != null)
                        Log($"内部异常: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
                    throw;
                }
            }
            finally
            {
                _transferLock.Release();
            }
        }

        private async Task ArchiveGameCoreAsync(Game game, IProgress<ArchiveProgress>? progress, CancellationToken ct)
        {
            if (game.IsRunning)
                throw new InvalidOperationException("游戏正在运行，无法归档");

            if (string.IsNullOrWhiteSpace(game.ExecutablePath) || !Directory.Exists(Path.GetDirectoryName(game.ExecutablePath)))
                throw new InvalidOperationException("游戏文件不存在，无法归档");

                // 先刷新 .gmd，保证归档包含最新元数据
                try
                {
                    await _gmdService.SerializeGameToGmdAsync(game, _imageService);
                    game.IsGmdFileReady = true;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Archive] 刷新 .gmd 失败（继续归档）: {ex.Message}");
                }

                var tempDir = Path.Combine(Path.GetTempPath(), "GameLauncher", "Archive", game.GameId);
                var workDir = Path.Combine(tempDir, "work");
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, true);
                Directory.CreateDirectory(workDir);
                _currentGameId = game.GameId;

                try
                {
                    // 1. 打包分卷
                    long totalOriginalSize;
                    int partCount;
                    (totalOriginalSize, partCount) = await Task.Run(() =>
                        PackVaultAsync(game, workDir, progress, ct));
                    Log($"打包完成: {partCount} 卷, {totalOriginalSize} 字节");

                    // 2. 上传各分卷（先上传全部新文件，成功后最后再删旧的，避免上传失败导致云端备份丢失）
                    var parentFolderId = await EnsureArchiveFolderAsync(ct);
                    Log($"归档目录就绪: {parentFolderId}");

                    var files = GetVaultPartFiles(workDir, partCount);
                    if (files.Count == 0)
                        throw new InvalidOperationException("游戏目录为空或没有可归档的文件，无法归档");

                    var uploadedParts = new List<CloudBackupPart>();
                    for (int i = 0; i < files.Count; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        var file = files[i];
                        var md5 = await ComputeFileMd5Async(file, ct);
                        var part = await UploadVaultPartAsync(game, file, md5, i + 1, partCount, progress, ct);
                        uploadedParts.Add(part);
                        Log($"第 {i + 1}/{partCount} 卷上传完成: fileID={part.FileId}");
                    }

                    // 3. 全部上传成功后，删除云端旧归档（重新归档 = 更新备份）
                    await DeleteOldCloudPartsAsync(game.GameId, parentFolderId, ct);

                    // 4. 写数据库备份标记
                    game.CloudArchivedAt = DateTime.UtcNow;
                    game.CloudBackupParts = uploadedParts;
                    game.CloudOriginalFolderName = Path.GetFileName(Path.GetDirectoryName(game.ExecutablePath)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) ?? game.GameId);
                    if (string.IsNullOrWhiteSpace(game.CloudOriginalFolderName))
                        game.CloudOriginalFolderName = game.GameId;
                    await _repository.UpdateCloudBackupAsync(game);
                    Log($"备份标记已写入数据库: {game.GameId}, 原目录名={game.CloudOriginalFolderName}");

                    progress?.Report(new ArchiveProgress
                    {
                        Stage = ArchiveProgress.TransferStage.Done,
                        PartIndex = partCount,
                        PartCount = partCount,
                        OverallPercent = 100,
                        CurrentFile = $"{game.Name} 归档成功（{FormatSize(uploadedParts.Sum(p => p.Size))}）"
                    });
                }
                finally
                {
                    try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
                }
        }

        private async Task<long> EnsureArchiveFolderAsync(CancellationToken ct)        {
            ct.ThrowIfCancellationRequested();
            if (_settings.Pan123ParentFolderId > 0)
                return _settings.Pan123ParentFolderId;

            // 先查根目录是否已有同名归档文件夹（123 云盘 mkdir 不允许同名，需复用已有目录）
            try
            {
                var rootFiles = await _pan123.ListFilesAsync(0);
                var existing = rootFiles.FirstOrDefault(f =>
                    f.GetType() == 1 && string.Equals(f.GetFileName(), VaultFolderName, StringComparison.OrdinalIgnoreCase));
                if (existing != null && existing.GetFileId() > 0)
                {
                    _settings.Pan123ParentFolderId = existing.GetFileId();
                    _settings.Save();
                    return _settings.Pan123ParentFolderId;
                }
            }
            catch (Exception ex)
            {
                Log($"查询云盘目录列表失败（继续尝试创建）: {ex.Message}");
            }

            var folderId = await _pan123.CreateFolderAsync(VaultFolderName, 0);
            if (folderId <= 0)
                throw new InvalidOperationException("创建云盘归档目录失败");
            _settings.Pan123ParentFolderId = folderId;
            _settings.Save();
            return folderId;
        }

        /// <summary>删除云端该游戏的所有旧归档分卷（重新归档 = 更新备份，不留旧文件）</summary>
        private async Task DeleteOldCloudPartsAsync(string gameId, long parentFolderId, CancellationToken ct)
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                var folderFiles = await _pan123.ListFilesAsync(parentFolderId);
                var oldParts = folderFiles
                    .Where(f => f.GetType() == 0 && IsVaultFileNameOf(f.GetFileName(), gameId))
                    .ToList();

                if (oldParts.Count == 0)
                    return;

                var ids = oldParts.Select(f => f.GetFileId()).Where(id => id > 0).ToList();
                if (ids.Count > 0)
                {
                    await _pan123.DeleteFilesAsync(ids);
                    Log($"已删除云端旧归档 {ids.Count} 个文件: {string.Join(", ", oldParts.Select(f => f.GetFileName()))}");
                }
            }
            catch (Exception ex)
            {
                Log($"删除云端旧归档失败（继续上传新归档）: {ex.Message}");
            }
        }

        private static bool IsVaultFileNameOf(string fileName, string gameId)
        {
            if (string.IsNullOrEmpty(fileName) || string.IsNullOrEmpty(gameId))
                return false;
            var prefix = gameId + VaultExtension;
            if (fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
            // 分卷名：{gid}.partN.vault
            var partPrefix = gameId + ".part";
            return fileName.StartsWith(partPrefix, StringComparison.OrdinalIgnoreCase) &&
                   fileName.EndsWith(VaultExtension, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>打包游戏目录为 .vault 分卷，返回 (原始总大小, 分卷数)</summary>
        private (long totalOriginalSize, int partCount) PackVaultAsync(Game game, string workDir,
            IProgress<ArchiveProgress>? progress, CancellationToken ct)
        {
            var gameDir = Path.GetDirectoryName(game.ExecutablePath)!;
            var files = Directory.EnumerateFiles(gameDir, "*", SearchOption.AllDirectories)
                .Where(f => !ShouldExclude(f, gameDir))
                .ToList();

            long totalOriginalSize = 0;
            foreach (var f in files)
                totalOriginalSize += new FileInfo(f).Length;

            var partFiles = new List<string>();
            var entryInfos = new List<(string relPath, long length, DateTime lastWrite)>();
            int partNo = 1;

            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                var rel = Path.GetRelativePath(gameDir, file).Replace('\\', '/');
                entryInfos.Add((rel, new FileInfo(file).Length, File.GetLastWriteTimeUtc(file)));
            }

            // 每卷流式写入 zip 条目，压缩后累计超过阈值即封卷
            var currentPart = new List<(string relPath, long length, DateTime lastWrite)>();
            long currentCompressed = 0;

            foreach (var entry in entryInfos)
            {
                if (currentPart.Count > 0 && currentCompressed + entry.length >= VaultMaxPartSize)
                {
                    FinishPart(workDir, gameDir, partNo, partFiles, currentPart, progress, ct);
                    partNo++;
                    currentPart.Clear();
                    currentCompressed = 0;
                }
                currentPart.Add(entry);
                currentCompressed += entry.length;
            }
            if (currentPart.Count > 0)
            {
                FinishPart(workDir, gameDir, partNo, partFiles, currentPart, progress, ct);
            }

            // 重写各卷头部（第 1 卷含元数据 + 分卷数/原始总大小；其余卷只含头部）
            var totalParts = partFiles.Count;
            for (int i = 0; i < partFiles.Count; i++)
            {
                RewriteVaultHeader(partFiles[i], totalOriginalSize, i + 1, totalParts);
            }

            return (totalOriginalSize, totalParts);
        }

        private void FinishPart(string workDir, string gameDir, int partNo, List<string> partFiles,
            List<(string relPath, long length, DateTime lastWrite)> entries,
            IProgress<ArchiveProgress>? progress, CancellationToken ct)
        {
            var name = partNo == 1 ? $"{_currentGameId}{VaultExtension}" : $"{_currentGameId}.part{partNo}{VaultExtension}";
            var fullPath = Path.Combine(workDir, name);
            partFiles.Add(fullPath);

            var header = BuildVaultHeader(0, partNo, 1); // 占位，稍后重写
            using (var fs = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            {
                fs.Write(header, 0, header.Length);
                using (var archive = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false))
                {
                    foreach (var entry in entries)
                    {
                        ct.ThrowIfCancellationRequested();
                        var zipEntry = archive.CreateEntry(entry.relPath, CompressionLevel.Optimal);
                        zipEntry.LastWriteTime = entry.lastWrite;
                        using (var entryStream = zipEntry.Open())
                        using (var src = new FileStream(Path.Combine(gameDir, entry.relPath.Replace('/', Path.DirectorySeparatorChar)), FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        {
                            src.CopyTo(entryStream, 1 << 20);
                        }
                    }
                }
            }
            progress?.Report(new ArchiveProgress
            {
                Stage = ArchiveProgress.TransferStage.Packaging,
                PartIndex = partNo,
                PartCount = 0, // 打包阶段还不知总卷数
                OverallPercent = 0,
                CurrentFile = $"正在打包 {name}..."
            });
        }

        private static bool ShouldExclude(string file, string gameDir)
        {
            var fileName = Path.GetFileName(file);
            if (fileName.StartsWith("~$", StringComparison.Ordinal))
                return true;
            if (fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                return true;

            var rel = Path.GetRelativePath(gameDir, file);
            var segments = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (segments.Any(s => s == ".sync"))
                return true;
            if (fileName.StartsWith("crash_log", StringComparison.OrdinalIgnoreCase))
                return true;

            // 排除本游戏的旧分卷（若归档目录恰好就在游戏目录里）
            if (fileName.EndsWith(VaultExtension, StringComparison.OrdinalIgnoreCase))
                return true;
            return false;
        }

        private static byte[] BuildVaultHeader(long originalTotalSize, int partIndex, int partCount)
        {
            var header = new byte[VaultHeaderSize];
            VaultSignature.CopyTo(header, 0);
            BitConverter.GetBytes((ushort)VaultFormatVersion).CopyTo(header, 5);
            BitConverter.GetBytes(originalTotalSize).CopyTo(header, 7);
            BitConverter.GetBytes(partIndex).CopyTo(header, 15);
            header[19] = (byte)partCount;
            return header;
        }

        private static void RewriteVaultHeader(string filePath, long originalTotalSize, int partIndex, int partCount)
        {
            var header = BuildVaultHeader(originalTotalSize, partIndex, partCount);
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Write, FileShare.None);
            fs.Write(header, 0, header.Length);
        }

        private static List<string> GetVaultPartFiles(string workDir, int partCount)
        {
            var files = Directory.GetFiles(workDir, $"*.{VaultExtension.TrimStart('.')}")
                .OrderBy(f => GetPartIndexFromName(Path.GetFileName(f)))
                .ToList();
            return files;
        }

        private static int GetPartIndexFromName(string fileName)
        {
            // GID.vault → 1；GID.part2.vault → 2
            var baseName = fileName.Replace(VaultExtension, string.Empty, StringComparison.OrdinalIgnoreCase);
            var dotIndex = baseName.LastIndexOf('.');
            if (dotIndex > 0 && baseName.Substring(dotIndex + 1).StartsWith("part", StringComparison.OrdinalIgnoreCase))
            {
                var numStr = baseName.Substring(dotIndex + 4);
                if (int.TryParse(numStr, out var n))
                    return n;
            }
            return 1;
        }

        private async Task<CloudBackupPart> UploadVaultPartAsync(Game game, string filePath, string md5,
            int partIndex, int partCount, IProgress<ArchiveProgress>? progress, CancellationToken ct)
        {
            var fileInfo = new FileInfo(filePath);
            var fileName = partIndex == 1 ? $"{game.GameId}{VaultExtension}" : $"{game.GameId}.part{partIndex}{VaultExtension}";

            var parentFolderId = await EnsureArchiveFolderAsync(ct);
            var createResult = await _pan123.CreateFileAsync(fileName, md5, fileInfo.Length, parentFolderId);

            if (createResult.reuse)
            {
                progress?.Report(new ArchiveProgress
                {
                    Stage = ArchiveProgress.TransferStage.Uploading,
                    PartIndex = partIndex,
                    PartCount = partCount,
                    OverallPercent = partIndex * 100.0 / partCount,
                    CurrentFile = $"{fileName}（秒传）"
                });
                return new CloudBackupPart(createResult.fileID, fileName, fileInfo.Length, md5, partIndex);
            }

            if (string.IsNullOrEmpty(createResult.preuploadID))
                throw new InvalidOperationException("预上传响应缺少 preuploadID");

            // 分片上传
            var servers = createResult.servers ?? await _pan123.GetUploadDomainsAsync();
            if (servers.Count == 0)
                throw new InvalidOperationException("无法获取上传域名");
            var server = servers[0];
            var sliceSize = createResult.sliceSize > 0 ? createResult.sliceSize : 16 * 1024 * 1024;

            await UploadSlicesAsync(filePath, server, createResult.preuploadID, sliceSize, game.GameId, fileName,
                partIndex, partCount, progress, ct);

            // 上传完毕 + 轮询（20103 校验中 → 隔 1 秒重试）
            long fileId = 0;
            var completeResult = await _pan123.UploadCompleteAsync(createResult.preuploadID);
            if (completeResult != null && completeResult.completed)
            {
                fileId = completeResult.fileID;
            }
            else
            {
                for (int attempt = 0; attempt < 60 && fileId == 0; attempt++)
                {
                    ct.ThrowIfCancellationRequested();
                    await Task.Delay(1000, ct);
                    var pollResult = await _pan123.UploadCompleteAsync(createResult.preuploadID);
                    if (pollResult != null && pollResult.completed)
                        fileId = pollResult.fileID;
                }
            }
            if (fileId == 0)
                throw new InvalidOperationException("上传完成确认超时（服务器校验未在 60 秒内完成）");

            progress?.Report(new ArchiveProgress
            {
                Stage = ArchiveProgress.TransferStage.Uploading,
                PartIndex = partIndex,
                PartCount = partCount,
                OverallPercent = partIndex * 100.0 / partCount,
                CurrentFile = $"{fileName} 上传完成"
            });

            return new CloudBackupPart(fileId, fileName, fileInfo.Length, md5, partIndex);
        }

        private async Task UploadSlicesAsync(string filePath, string server, string preuploadID, long sliceSize,
            string gameId, string fileName, int partIndex, int partCount,
            IProgress<ArchiveProgress>? progress, CancellationToken ct)
        {
            var fileInfo = new FileInfo(filePath);
            long totalSlices = (fileInfo.Length + sliceSize - 1) / sliceSize;
            var completedSlices = 0L;
            var semaphore = new SemaphoreSlim(SliceConcurrency);
            var errors = new System.Collections.Concurrent.ConcurrentQueue<string>();

            // 预计算每片 MD5（顺序读取，避免多线程重复开文件）
            var sliceMd5s = new Dictionary<int, string>();
            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var buffer = new byte[(int)sliceSize];
                int sliceNo = 1;
                int read;
                while ((read = ReadUpTo(fs, buffer, (int)sliceSize)) > 0)
                {
                    var hashBytes = MD5.HashData(buffer.AsSpan(0, read));
                    sliceMd5s[sliceNo] = Convert.ToHexString(hashBytes).ToLowerInvariant();
                    sliceNo++;
                }
            }
            Log($"预计算 {sliceMd5s.Count} 片 MD5, totalSlices={totalSlices}, sliceSize={sliceSize}, fileSize={fileInfo.Length}");

            var tasks = new List<Task>();
            for (int sliceNo = 1; sliceNo <= totalSlices; sliceNo++)
            {
                ct.ThrowIfCancellationRequested();
                await semaphore.WaitAsync(ct);
                var currentSliceNo = sliceNo; // for 循环闭包陷阱：必须拷贝局部变量
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        var sliceData = await ReadSliceAsync(filePath, (currentSliceNo - 1) * sliceSize, sliceSize);
                        if (!sliceMd5s.TryGetValue(currentSliceNo, out var sliceMd5))
                            throw new InvalidOperationException($"分片 MD5 缺失（sliceNo={currentSliceNo}）");
                        await _pan123.UploadSliceAsync(server, preuploadID, currentSliceNo, sliceMd5, sliceData);
                        Interlocked.Increment(ref completedSlices);
                        progress?.Report(new ArchiveProgress
                        {
                            Stage = ArchiveProgress.TransferStage.Uploading,
                            PartIndex = partIndex,
                            PartCount = partCount,
                            OverallPercent = ((partIndex - 1) + completedSlices * 1.0 / totalSlices) * 100.0 / partCount,
                            CurrentFile = $"{fileName}（{completedSlices}/{totalSlices} 分片）"
                        });
                    }
                    catch (Exception ex)
                    {
                        errors.Enqueue(ex.Message);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }));
            }
            await Task.WhenAll(tasks);

            if (!errors.IsEmpty)
                throw new InvalidOperationException($"分片上传失败: {errors.First()}");
        }

        private static int ReadUpTo(Stream stream, byte[] buffer, int count)
        {
            int total = 0;
            while (total < count)
            {
                var read = stream.Read(buffer, total, count - total);
                if (read == 0) break;
                total += read;
            }
            return total;
        }

        private static async Task<byte[]> ReadSliceAsync(string filePath, long offset, long count)
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            fs.Seek(offset, SeekOrigin.Begin);
            var buffer = new byte[count];
            int total = 0;
            while (total < count)
            {
                var read = await fs.ReadAsync(buffer, total, (int)Math.Min(count - total, 1 << 20));
                if (read == 0) break;
                total += read;
            }
            Array.Resize(ref buffer, total);
            return buffer;
        }

        private static async Task<string> ComputeFileMd5Async(string filePath, CancellationToken ct)
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var hash = await MD5.HashDataAsync(fs, ct);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        // ---------- 下载恢复 ----------

        public async Task DownloadGameAsync(Game game, IProgress<ArchiveProgress>? progress = null, CancellationToken ct = default)
        {
            await _transferLock.WaitAsync(ct);
            try
            {
                try
                {
                    await DownloadGameCoreAsync(game, progress, ct);
                }
                catch (Exception ex)
                {
                    Log($"下载失败: {ex.GetType().Name}: {ex.Message}");
                    if (ex.InnerException != null)
                        Log($"内部异常: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
                    throw;
                }
            }
            finally
            {
                _transferLock.Release();
            }
        }

        private async Task DownloadGameCoreAsync(Game game, IProgress<ArchiveProgress>? progress, CancellationToken ct)
        {
            var parts = game.CloudBackupParts?.Where(p => p.FileId > 0).OrderBy(p => p.PartIndex).ToList()
                ?? new List<CloudBackupPart>();
            if (parts.Count == 0)
                throw new InvalidOperationException("该游戏没有云备份记录，无法下载");

            var tempDir = Path.Combine(Path.GetTempPath(), "GameLauncher", "Download", game.GameId);
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
            Directory.CreateDirectory(tempDir);

            try
            {
                // 1. 下载全部分卷
                var localFiles = new List<string>();
                for (int i = 0; i < parts.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var part = parts[i];
                    progress?.Report(new ArchiveProgress
                    {
                        Stage = ArchiveProgress.TransferStage.Downloading,
                        PartIndex = i + 1,
                        PartCount = parts.Count,
                        OverallPercent = i * 100.0 / parts.Count,
                        CurrentFile = $"正在下载 {part.FileName}..."
                    });
                    var localPath = Path.Combine(tempDir, part.FileName);
                    await DownloadVaultPartAsync(part, localPath, parts.Count, i + 1, progress, ct);
                    localFiles.Add(localPath);
                }
                Log($"分卷下载完成: {localFiles.Count} 个文件");

                // 2. 校验 + 解压到游戏目录
                progress?.Report(new ArchiveProgress
                {
                    Stage = ArchiveProgress.TransferStage.Downloading,
                    PartIndex = parts.Count,
                    PartCount = parts.Count,
                    OverallPercent = 100,
                    CurrentFile = "正在解压..."
                });
                var extractDir = await ExtractVaultAsync(game, localFiles, parts, ct);
                Log($"解压完成: {extractDir}");

                // 3. 入库防重复：按 gid 匹配，存在则更新、不存在才新建
                var restoredGame = await RestoreGameAsync(game, extractDir);
                Log($"入库完成: gid={restoredGame.GameId}, exe={restoredGame.ExecutablePath}");

                // 4. 同步回内存中的 game 对象，让 UI（卡片按钮状态）立即生效
                if (!string.IsNullOrWhiteSpace(restoredGame.ExecutablePath))
                    game.ExecutablePath = restoredGame.ExecutablePath;
                if (!string.IsNullOrWhiteSpace(restoredGame.IconPath))
                    game.IconPath = restoredGame.IconPath;
                if (!string.IsNullOrWhiteSpace(restoredGame.GmdFilePath))
                    game.GmdFilePath = restoredGame.GmdFilePath;
                game.Name = restoredGame.Name;

                progress?.Report(new ArchiveProgress
                {
                    Stage = ArchiveProgress.TransferStage.Done,
                    PartIndex = parts.Count,
                    PartCount = parts.Count,
                    OverallPercent = 100,
                    CurrentFile = $"{game.Name} 恢复完成"
                });
            }
            finally
            {
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
            }
        }

        private async Task DownloadVaultPartAsync(CloudBackupPart part, string localPath, int partCount, int partIndex,
            IProgress<ArchiveProgress>? progress, CancellationToken ct)
        {
            var info = await _pan123.GetDownloadInfoAsync(part.FileId);
            if (string.IsNullOrEmpty(info.downloadUrl))
                throw new InvalidOperationException($"获取 {part.FileName} 下载地址失败");

            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            using var response = await http.GetAsync(info.downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            var totalBytes = response.Content.Headers.ContentLength ?? part.Size;

            await using (var source = await response.Content.ReadAsStreamAsync(ct))
            using (var dest = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[1 << 20];
                long received = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    await dest.WriteAsync(buffer.AsMemory(0, read), ct);
                    received += read;
                    progress?.Report(new ArchiveProgress
                    {
                        Stage = ArchiveProgress.TransferStage.Downloading,
                        PartIndex = partIndex,
                        PartCount = partCount,
                        OverallPercent = ((partIndex - 1) + received * 1.0 / Math.Max(totalBytes, 1)) * 100.0 / partCount,
                        CurrentFile = $"{part.FileName}（{FormatSize(received)}/{FormatSize(totalBytes)}）"
                    });
                }
            } // 关闭写入流后再校验，避免文件被占用

            // 校验大小与魔数
            var fileInfo = new FileInfo(localPath);
            if (fileInfo.Length != part.Size)
                throw new InvalidOperationException($"{part.FileName} 大小不一致（预期 {part.Size}，实际 {fileInfo.Length}），归档可能已损坏");

            ValidateVaultHeader(localPath, part.PartIndex, partCount);
        }

        private static void ValidateVaultHeader(string filePath, int partIndex, int partCount)
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var header = new byte[VaultHeaderSize];
            var read = ReadUpTo(fs, header, VaultHeaderSize);
            if (read < VaultHeaderSize)
                throw new InvalidOperationException("归档文件损坏（文件头不完整）");

            for (int i = 0; i < VaultSignature.Length; i++)
            {
                if (header[i] != VaultSignature[i])
                    throw new InvalidOperationException("归档文件损坏或不是有效的 GameLauncher 归档（魔数校验失败）");
            }
            var version = BitConverter.ToUInt16(header, 5);
            if (version != VaultFormatVersion)
                throw new InvalidOperationException($"归档版本不受支持（版本 {version}）");
        }

        /// <summary>解压分卷到目标目录，返回解压目录</summary>
        private async Task<string> ExtractVaultAsync(Game game, List<string> localFiles, List<CloudBackupPart> parts, CancellationToken ct)
        {
            var libraryPath = _settings.GameLibraryPath;
            if (string.IsNullOrWhiteSpace(libraryPath))
                libraryPath = Path.GetDirectoryName(game.ExecutablePath) ?? Path.GetTempPath();

            // 优先用归档时的原目录名解压，保持「游戏目录\原文件夹名\游戏文件」结构
            var folderName = !string.IsNullOrWhiteSpace(game.CloudOriginalFolderName)
                ? game.CloudOriginalFolderName
                : game.GameId;

            // 安全校验：folderName 必须是合法单层目录名（防路径穿越删除任意目录）
            var invalidChars = Path.GetInvalidFileNameChars();
            if (folderName.IndexOfAny(invalidChars) >= 0 ||
                folderName == "." || folderName == ".." ||
                folderName.Contains(Path.DirectorySeparatorChar) ||
                folderName.Contains(Path.AltDirectorySeparatorChar) ||
                string.IsNullOrWhiteSpace(folderName))
            {
                Log($"非法目录名，回退为 gid: {folderName}");
                folderName = game.GameId;
            }

            var extractDir = Path.GetFullPath(Path.Combine(libraryPath, folderName));
            var libraryRoot = Path.GetFullPath(libraryPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!extractDir.StartsWith(libraryRoot, StringComparison.OrdinalIgnoreCase))
            {
                Log($"解压路径越界，回退为 gid 目录: {extractDir}");
                extractDir = Path.Combine(libraryPath, game.GameId);
            }
            if (Directory.Exists(extractDir))
                Directory.Delete(extractDir, true);
            Directory.CreateDirectory(extractDir);

            try
            {
                for (int i = 0; i < localFiles.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var file = localFiles[i];
                    var part = parts[i];
                    var expectedIndex = i + 1;
                    var actualIndex = ReadPartIndexFromHeader(file);
                    if (actualIndex != expectedIndex)
                        throw new InvalidOperationException($"分卷顺序不匹配（第 {expectedIndex} 卷内容为第 {actualIndex} 卷）");

                    var zipOffset = VaultHeaderSize;
                    await using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                    fs.Seek(zipOffset, SeekOrigin.Begin);
                    using var archive = new ZipArchive(fs, ZipArchiveMode.Read);
                    foreach (var entry in archive.Entries)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (entry.FullName == VaultMetadataEntry)
                            continue;

                        var safeRelative = entry.FullName.Replace('\\', '/');
                        if (safeRelative.StartsWith("/", StringComparison.Ordinal) ||
                            Path.IsPathRooted(safeRelative))
                            throw new InvalidOperationException($"归档包含非法路径: {entry.FullName}");

                        // ZipSlip 防护：规范化后必须仍位于解压目录内
                        var targetPath = Path.GetFullPath(Path.Combine(extractDir, safeRelative));
                        var rootPath = Path.GetFullPath(extractDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                        if (!targetPath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException($"归档路径越界: {entry.FullName}");

                        var targetDir = Path.GetDirectoryName(targetPath);
                        if (!string.IsNullOrEmpty(targetDir))
                            Directory.CreateDirectory(targetDir);

                        if (entry.Length == 0 && (entry.FullName.EndsWith("/", StringComparison.Ordinal)))
                            continue;

                        await using var entryStream = entry.Open();
                        await using var fileStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None);
                        await entryStream.CopyToAsync(fileStream, ct);
                    }
                }

                return extractDir;
            }
            catch
            {
                try { if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true); } catch { }
                throw;
            }
        }

        private static int ReadPartIndexFromHeader(string filePath)
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var header = new byte[VaultHeaderSize];
            if (ReadUpTo(fs, header, VaultHeaderSize) < VaultHeaderSize)
                throw new InvalidOperationException("归档文件损坏（文件头不完整）");
            return BitConverter.ToInt32(header, 15);
        }

        private async Task<Game> RestoreGameAsync(Game source, string extractDir)
        {
            var gmdFiles = Directory.GetFiles(extractDir, "*.gmd", SearchOption.AllDirectories);
            var existing = await _repository.GetGameByGameIdAsync(source.GameId);

            if (existing != null)
            {
                // 已存在：不新建，仅更新路径信息
                if (gmdFiles.Length > 0)
                {
                    var deserialized = await _gmdService.DeserializeGameFromGmdAsync(gmdFiles[0]);
                    if (deserialized != null)
                    {
                        existing.Name = deserialized.Name;
                        existing.ExecutablePath = deserialized.ExecutablePath;
                        existing.IconPath = deserialized.IconPath;
                        existing.Description = deserialized.Description;
                        existing.Tags = deserialized.Tags;
                    }
                }
                existing.CloudArchivedAt = source.CloudArchivedAt;
                existing.CloudBackupParts = source.CloudBackupParts;
                if (!string.IsNullOrWhiteSpace(source.CloudOriginalFolderName))
                    existing.CloudOriginalFolderName = source.CloudOriginalFolderName;
                await _repository.UpdateGameAsync(existing);
                Debug.WriteLine($"[Archive] 游戏已存在（gid={source.GameId}），已更新本地路径，未创建重复记录");
                return existing;
            }

            // 不存在：从 .gmd 解析导入（三重去重：gid/name/exe 已由 GetGameByGameId 覆盖 gid，这里再做 name/exe 检查）
            if (gmdFiles.Length > 0)
            {
                var deserialized = await _gmdService.DeserializeGameFromGmdAsync(gmdFiles[0]);
                if (deserialized != null)
                {
                    var all = await _repository.GetAllGamesAsync();
                    var nameDup = all.FirstOrDefault(g =>
                        string.Equals(g.Name, deserialized.Name, StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrWhiteSpace(g.Name));
                    var exeDup = all.FirstOrDefault(g =>
                        string.Equals(g.ExecutablePath, deserialized.ExecutablePath, StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrWhiteSpace(g.ExecutablePath));
                    if (nameDup != null || exeDup != null)
                    {
                        var target = nameDup ?? exeDup!;
                        target.CloudArchivedAt = source.CloudArchivedAt;
                        target.CloudBackupParts = source.CloudBackupParts;
                        if (!string.IsNullOrWhiteSpace(source.CloudOriginalFolderName))
                            target.CloudOriginalFolderName = source.CloudOriginalFolderName;
                        await _repository.UpdateGameAsync(target);
                        return target;
                    }

                    deserialized.CloudArchivedAt = source.CloudArchivedAt;
                    deserialized.CloudBackupParts = source.CloudBackupParts;
                    if (!string.IsNullOrWhiteSpace(source.CloudOriginalFolderName))
                        deserialized.CloudOriginalFolderName = source.CloudOriginalFolderName;
                    await _repository.AddGameAsync(deserialized);
                    return deserialized;
                }
            }

            // 兜底：直接用源记录恢复
            source.ExecutablePath = gmdFiles.Length > 0
                ? FindExecutableNearGmd(gmdFiles[0])
                : FindExecutableInDir(extractDir) ?? source.ExecutablePath;
            source.IconPath = string.Empty;
            await _repository.UpdateGameAsync(source);
            return source;
        }

        private static string? FindExecutableNearGmd(string gmdPath)
        {
            var dir = Path.GetDirectoryName(gmdPath);
            return FindExecutableInDir(dir);
        }

        private static string? FindExecutableInDir(string? dir)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                return null;
            return Directory.GetFiles(dir, "*.exe", SearchOption.TopDirectoryOnly)
                .OrderByDescending(f => new FileInfo(f).Length)
                .FirstOrDefault();
        }

        private static string FormatSize(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.#} KB";
            if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):0.#} MB";
            return $"{bytes / (1024.0 * 1024 * 1024):0.##} GB";
        }
    }
}
