using GameLauncher.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace GameLauncher.Services
{
    public class ScanProgress
    {
        public string CurrentDrive { get; set; } = string.Empty;
        public int FoundCount { get; set; }
        public int SkippedCount { get; set; }
        public int ErrorCount { get; set; }
        public string Message { get; set; } = string.Empty;
        public double Percentage { get; set; }
    }

    public class ScanResult
    {
        public List<Game> DiscoveredGames { get; set; } = new List<Game>();
        public List<Game> ExistingGames { get; set; } = new List<Game>();
        public List<string> FailedFiles { get; set; } = new List<string>();
    }

    public class DiskScanService
    {
        private readonly GmdFileService _gmdService;
        private readonly ImageService _imageService;
        private static readonly HashSet<string> SkipDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Windows",
            "Program Files",
            "Program Files (x86)",
            "ProgramData",
            "$Recycle.Bin",
            "System Volume Information",
            "node_modules"
        };

        public DiskScanService(GmdFileService gmdService, ImageService imageService)
        {
            _gmdService = gmdService;
            _imageService = imageService;
        }

        public List<DriveInfo> GetAvailableDrives()
        {
            var drives = new List<DriveInfo>();

            try
            {
                foreach (var drive in DriveInfo.GetDrives())
                {
                    try
                    {
                        if (drive.IsReady && drive.DriveType != DriveType.Unknown && drive.DriveType != DriveType.NoRootDirectory)
                        {
                            drives.Add(drive);
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[DiskScanService] 获取驱动器信息失败: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DiskScanService] 枚举驱动器失败: {ex.Message}");
            }

            return drives;
        }

        public async Task<List<string>> ScanDriveForGmdFilesAsync(string driveRoot, CancellationToken ct)
        {
            var result = new List<string>();

            await Task.Run(() =>
            {
                try
                {
                    if (!Directory.Exists(driveRoot))
                    {
                        Debug.WriteLine($"[DiskScanService] 驱动器根目录不存在: {driveRoot}");
                        return;
                    }

                    EnumerateGmdFilesRecursive(driveRoot, result, ct);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[DiskScanService] 扫描驱动器失败: {driveRoot}, 错误: {ex.Message}");
                }
            }, ct);

            return result;
        }

        private void EnumerateGmdFilesRecursive(string directory, List<string> result, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var options = new EnumerationOptions
                {
                    RecurseSubdirectories = false,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.Hidden | FileAttributes.System
                };

                foreach (var filePath in Directory.EnumerateFiles(directory, "*.gmd", options))
                {
                    result.Add(filePath);
                    Debug.WriteLine($"[DiskScanService] 发现.gmd文件: {filePath}");
                }

                foreach (var subDir in Directory.EnumerateDirectories(directory, "*", options))
                {
                    var dirName = Path.GetFileName(subDir);
                    if (!SkipDirectories.Contains(dirName))
                    {
                        EnumerateGmdFilesRecursive(subDir, result, ct);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DiskScanService] 扫描目录失败: {directory}, 错误: {ex.Message}");
            }
        }

        public async Task<(string? iconPath, List<string> previewPaths)> ExtractImagesFromGmdToLocalAsync(string gmdFilePath, string gameId)
        {
            string? iconPath = null;
            List<string> previewPaths = new List<string>();

            try
            {
                var tempDir = Path.Combine(Path.GetTempPath(), "GameLauncher", "GmdExtract", Guid.NewGuid().ToString());

                try
                {
                    if (!Directory.Exists(tempDir))
                        Directory.CreateDirectory(tempDir);

                    using (var archive = System.IO.Compression.ZipFile.OpenRead(gmdFilePath))
                    {
                        var iconEntry = archive.GetEntry("icon.jpg");
                        if (iconEntry != null)
                        {
                            var tempIconPath = Path.Combine(tempDir, "icon.jpg");
                            iconEntry.ExtractToFile(tempIconPath, overwrite: true);
                            if (System.IO.File.Exists(tempIconPath))
                            {
                                iconPath = await _imageService.SaveIconAsync(gameId, tempIconPath);
                            }
                        }

                        int index = 1;
                        foreach (var entry in archive.Entries)
                        {
                            if ((entry.FullName.StartsWith("images/", StringComparison.OrdinalIgnoreCase) ||
                                 entry.FullName.StartsWith("images\\", StringComparison.OrdinalIgnoreCase)) &&
                                !entry.FullName.EndsWith("/") && !entry.FullName.EndsWith("\\"))
                            {
                                var fileName = Path.GetFileName(entry.FullName);
                                if (string.IsNullOrEmpty(fileName)) continue;
                                var tempPath = Path.Combine(tempDir, fileName);
                                entry.ExtractToFile(tempPath, overwrite: true);
                                if (System.IO.File.Exists(tempPath))
                                {
                                    var savedPath = await _imageService.SavePreviewImageAsync(gameId, tempPath, index);
                                    if (!string.IsNullOrEmpty(savedPath))
                                    {
                                        previewPaths.Add(savedPath);
                                    }
                                    index++;
                                }
                            }
                        }
                    }
                }
                finally
                {
                    if (Directory.Exists(tempDir))
                    {
                        try { Directory.Delete(tempDir, true); } catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DiskScanService] 提取图片失败: {gmdFilePath}, 错误: {ex.Message}");
            }

            return (iconPath, previewPaths);
        }

        public async Task<Game?> ResolveGameFromGmdAsync(string gmdFilePath)
        {
            try
            {
                var game = await _gmdService.DeserializeGameFromGmdAsync(gmdFilePath);
                if (game == null) return null;

                game.GmdFilePath = gmdFilePath;

                _imageService.EnsureGameImageDirectory(game.GameId);

                var (iconPath, _) = await ExtractImagesFromGmdToLocalAsync(gmdFilePath, game.GameId);
                if (!string.IsNullOrEmpty(iconPath))
                {
                    game.IconPath = iconPath;
                }

                Debug.WriteLine($"[DiskScanService] 成功解析.gmd: {gmdFilePath}");
                return game;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DiskScanService] 解析.gmd失败: {gmdFilePath}, 错误: {ex.Message}");
                return null;
            }
        }

        public async Task<ScanResult> FullScanAsync(IEnumerable<Game> existingGames, IProgress<ScanProgress> progress, CancellationToken ct)
        {
            var result = new ScanResult();
            var existingGameSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var existingGameIdSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var game in existingGames)
            {
                if (!string.IsNullOrEmpty(game.ExecutablePath))
                {
                    existingGameSet.Add(game.ExecutablePath);
                }
                if (!string.IsNullOrWhiteSpace(game.GameId))
                {
                    existingGameIdSet.Add(game.GameId);
                }
            }

            var drives = GetAvailableDrives();
            var totalDrives = drives.Count;

            for (int driveIndex = 0; driveIndex < drives.Count; driveIndex++)
            {
                ct.ThrowIfCancellationRequested();

                var drive = drives[driveIndex];
                var driveRoot = drive.RootDirectory.FullName;

                var scanProgress = new ScanProgress
                {
                    CurrentDrive = driveRoot,
                    FoundCount = result.DiscoveredGames.Count,
                    SkippedCount = result.ExistingGames.Count,
                    ErrorCount = result.FailedFiles.Count,
                    Message = $"正在扫描驱动器 {driveRoot}...",
                    Percentage = (double)driveIndex / totalDrives * 100.0
                };
                progress.Report(scanProgress);

                Debug.WriteLine($"[DiskScanService] 开始扫描驱动器: {driveRoot}");

                List<string> gmdFiles;
                try
                {
                    gmdFiles = await ScanDriveForGmdFilesAsync(driveRoot, ct);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[DiskScanService] 扫描驱动器失败: {driveRoot}, 错误: {ex.Message}");
                    continue;
                }

                Debug.WriteLine($"[DiskScanService] 驱动器 {driveRoot} 发现 {gmdFiles.Count} 个.gmd文件");

                for (int fileIndex = 0; fileIndex < gmdFiles.Count; fileIndex++)
                {
                    ct.ThrowIfCancellationRequested();

                    var gmdFile = gmdFiles[fileIndex];
                    var processingProgress = new ScanProgress
                    {
                        CurrentDrive = driveRoot,
                        FoundCount = result.DiscoveredGames.Count,
                        SkippedCount = result.ExistingGames.Count,
                        ErrorCount = result.FailedFiles.Count,
                        Message = $"正在处理 ({fileIndex + 1}/{gmdFiles.Count}): {Path.GetFileName(gmdFile)}",
                        Percentage = ((double)driveIndex + ((double)fileIndex / Math.Max(gmdFiles.Count, 1))) / totalDrives * 100.0
                    };
                    progress.Report(processingProgress);

                    var game = await ResolveGameFromGmdAsync(gmdFile);

                    if (game == null)
                    {
                        result.FailedFiles.Add(gmdFile);
                        Debug.WriteLine($"[DiskScanService] 解析.gmd失败，已加入失败列表: {gmdFile}");
                        continue;
                    }

                    if (existingGameSet.Contains(game.ExecutablePath) ||
                        (!string.IsNullOrWhiteSpace(game.GameId) && existingGameIdSet.Contains(game.GameId)))
                    {
                        result.ExistingGames.Add(game);
                        Debug.WriteLine($"[DiskScanService] 游戏已存在于库中: {game.Name} ({game.ExecutablePath})");
                    }
                    else
                    {
                        result.DiscoveredGames.Add(game);
                        Debug.WriteLine($"[DiskScanService] 发现新游戏: {game.Name} ({game.ExecutablePath})");
                    }
                }
            }

            var finalProgress = new ScanProgress
            {
                CurrentDrive = string.Empty,
                FoundCount = result.DiscoveredGames.Count,
                SkippedCount = result.ExistingGames.Count,
                ErrorCount = result.FailedFiles.Count,
                Message = $"扫描完成。发现 {result.DiscoveredGames.Count} 个新游戏，{result.ExistingGames.Count} 个已存在，{result.FailedFiles.Count} 个失败。",
                Percentage = 100.0
            };
            progress.Report(finalProgress);

            Debug.WriteLine($"[DiskScanService] 全盘扫描完成。新游戏: {result.DiscoveredGames.Count}, 已存在: {result.ExistingGames.Count}, 失败: {result.FailedFiles.Count}");

            return result;
        }
    }
}