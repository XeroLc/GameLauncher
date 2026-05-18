using GameLauncher.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace GameLauncher.Services
{
    public class GmdFileService
    {
        private const string MetadataFileName = "metadata.json";
        private const string IconFileName = "icon.jpg";
        private const string ImagesDirectoryName = "images";

        private static readonly ConcurrentDictionary<string, SemaphoreSlim> _fileLocks = new(StringComparer.OrdinalIgnoreCase);

        public string GetGmdFilePath(string executablePath, string gameId)
        {
            if (string.IsNullOrWhiteSpace(executablePath))
                throw new ArgumentException("可执行文件路径不能为空", nameof(executablePath));
            if (string.IsNullOrWhiteSpace(gameId))
                throw new ArgumentException("游戏ID不能为空", nameof(gameId));

            var directory = Path.GetDirectoryName(executablePath) ?? throw new InvalidOperationException("无法获取可执行文件目录");
            return Path.Combine(directory, $"{gameId}.gmd");
        }

        public bool GmdFileExists(string gmdFilePath)
        {
            if (string.IsNullOrWhiteSpace(gmdFilePath))
                throw new ArgumentException(".gmd文件路径不能为空", nameof(gmdFilePath));

            return File.Exists(gmdFilePath);
        }

        public async Task SerializeGameToGmdAsync(Game game)
        {
            if (game == null)
                throw new ArgumentNullException(nameof(game));
            if (string.IsNullOrWhiteSpace(game.ExecutablePath))
                throw new ArgumentException("游戏可执行文件路径不能为空");
            if (string.IsNullOrWhiteSpace(game.Name))
                throw new ArgumentException("游戏名称不能为空");

            var gmdFilePath = GetGmdFilePath(game.ExecutablePath, game.GameId);
            var directory = Path.GetDirectoryName(gmdFilePath) ?? throw new InvalidOperationException("无法获取.gmd文件目录");

            if (!Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            var semaphore = _fileLocks.GetOrAdd(gmdFilePath, _ => new SemaphoreSlim(1, 1));
            await semaphore.WaitAsync();
            try
            {
                await Task.Run(async () =>
                {
                    if (File.Exists(gmdFilePath))
                    {
                        File.Delete(gmdFilePath);
                    }

                    using (var archive = ZipFile.Open(gmdFilePath, ZipArchiveMode.Create))
                    {
                        var metadata = CreateMetadataFromGame(game);
                        var json = JsonSerializer.Serialize(metadata, new JsonSerializerOptions
                        {
                            WriteIndented = true,
                            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                        });

                        var metadataEntry = archive.CreateEntry(MetadataFileName);
                        using (var entryStream = metadataEntry.Open())
                        using (var writer = new StreamWriter(entryStream))
                        {
                            await writer.WriteAsync(json);
                        }

                        await AddIconToArchiveAsync(archive, game.IconPath);
                        await AddImagesToArchiveAsync(archive, game.ImagePaths);
                    }

                    game.GmdFilePath = gmdFilePath;
                    game.IsGmdFileReady = true;

                    Debug.WriteLine($"[GmdFileService] 成功创建.gmd文件: {gmdFilePath}");
                });
            }
            finally
            {
                semaphore.Release();
                _fileLocks.TryRemove(gmdFilePath, out _);
            }
        }

        public async Task<Game> DeserializeGameFromGmdAsync(string gmdFilePath)
        {
            if (string.IsNullOrWhiteSpace(gmdFilePath))
                throw new ArgumentException(".gmd文件路径不能为空", nameof(gmdFilePath));
            if (!File.Exists(gmdFilePath))
                throw new FileNotFoundException(".gmd文件不存在", gmdFilePath);

            return await Task.Run(async () =>
            {
                var tempDir = Path.Combine(Path.GetTempPath(), "GameLauncher", "GmdExtract", Guid.NewGuid().ToString());
                var imageService = new ImageService();

                try
                {
                    using (var archive = ZipFile.OpenRead(gmdFilePath))
                    {
                        var metadataEntry = archive.GetEntry(MetadataFileName);
                        if (metadataEntry == null)
                            throw new InvalidDataException(".gmd文件缺少metadata.json");

                        string json;
                        using (var stream = metadataEntry.Open())
                        using (var reader = new StreamReader(stream))
                        {
                            json = await reader.ReadToEndAsync();
                        }

                        var metadata = JsonSerializer.Deserialize<GmdMetadata>(json);
                        if (metadata == null)
                            throw new InvalidDataException("无法解析metadata.json");

                        var game = CreateGameFromMetadata(metadata, gmdFilePath);

                        var gameId = game.GameId;
                        if (string.IsNullOrWhiteSpace(gameId))
                        {
                            gameId = Path.GetFileNameWithoutExtension(gmdFilePath);
                        }

                        var iconEntry = archive.GetEntry(IconFileName);
                        if (iconEntry != null)
                        {
                            var tempIconPath = ExtractEntryToTemp(archive, iconEntry, tempDir, IconFileName);
                            if (!string.IsNullOrEmpty(tempIconPath) && File.Exists(tempIconPath))
                            {
                                var savedIconPath = await imageService.SaveIconAsync(gameId, tempIconPath);
                                if (!string.IsNullOrEmpty(savedIconPath))
                                {
                                    game.IconPath = savedIconPath;
                                }
                            }
                        }

                        var imageEntries = new List<ZipArchiveEntry>();
                        foreach (var entry in archive.Entries)
                        {
                            if (entry.FullName.StartsWith(ImagesDirectoryName + "/", StringComparison.OrdinalIgnoreCase) ||
                                entry.FullName.StartsWith(ImagesDirectoryName + "\\", StringComparison.OrdinalIgnoreCase))
                            {
                                if (!entry.FullName.EndsWith("/") && !entry.FullName.EndsWith("\\"))
                                {
                                    imageEntries.Add(entry);
                                }
                            }
                        }

                        imageEntries.Sort((a, b) => string.Compare(a.FullName, b.FullName, StringComparison.OrdinalIgnoreCase));

                        int previewIndex = 1;
                        foreach (var entry in imageEntries)
                        {
                            var tempImagePath = ExtractEntryToTemp(archive, entry, tempDir, entry.FullName);
                            if (!string.IsNullOrEmpty(tempImagePath) && File.Exists(tempImagePath))
                            {
                                var savedPath = await imageService.SavePreviewImageAsync(gameId, tempImagePath, previewIndex);
                                if (!string.IsNullOrEmpty(savedPath))
                                {
                                    game.ImagePaths.Add(savedPath);
                                }
                                previewIndex++;
                            }
                        }

                        game.GmdFilePath = gmdFilePath;
                        game.IsGmdFileReady = true;

                        Debug.WriteLine($"[GmdFileService] 成功从.gmd文件加载游戏: {gmdFilePath}, 图片数量: {game.ImagePaths.Count}");

                        return game;
                    }
                }
                catch
                {
                    throw;
                }
                finally
                {
                    if (Directory.Exists(tempDir))
                    {
                        try
                        {
                            Directory.Delete(tempDir, true);
                        }
                        catch
                        {
                        }
                    }
                }
            });
        }

        public void DeleteGmdFile(string gmdFilePath)
        {
            if (string.IsNullOrWhiteSpace(gmdFilePath))
                throw new ArgumentException(".gmd文件路径不能为空", nameof(gmdFilePath));

            if (File.Exists(gmdFilePath))
            {
                File.Delete(gmdFilePath);
                Debug.WriteLine($"[GmdFileService] 已删除.gmd文件: {gmdFilePath}");
            }
        }

        public static string? ExtractIconFromGmd(string gmdFilePath)
        {
            if (string.IsNullOrWhiteSpace(gmdFilePath) || !File.Exists(gmdFilePath))
                return null;

            try
            {
                var tempDir = Path.Combine(Path.GetTempPath(), "GameLauncher", "GmdFallback", Path.GetFileNameWithoutExtension(gmdFilePath));
                if (!Directory.Exists(tempDir))
                    Directory.CreateDirectory(tempDir);

                using var archive = ZipFile.OpenRead(gmdFilePath);
                var iconEntry = archive.GetEntry(IconFileName);
                if (iconEntry == null)
                    return null;

                var targetPath = Path.Combine(tempDir, IconFileName);
                iconEntry.ExtractToFile(targetPath, overwrite: true);
                return targetPath;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GmdFileService] 从.gmd提取图标失败: {ex.Message}");
                return null;
            }
        }

        public static List<string> ExtractImagesFromGmd(string gmdFilePath)
        {
            var result = new List<string>();

            if (string.IsNullOrWhiteSpace(gmdFilePath) || !File.Exists(gmdFilePath))
                return result;

            try
            {
                var tempDir = Path.Combine(Path.GetTempPath(), "GameLauncher", "GmdFallback", Path.GetFileNameWithoutExtension(gmdFilePath));
                if (!Directory.Exists(tempDir))
                    Directory.CreateDirectory(tempDir);

                using var archive = ZipFile.OpenRead(gmdFilePath);
                foreach (var entry in archive.Entries)
                {
                    if ((entry.FullName.StartsWith(ImagesDirectoryName + "/", StringComparison.OrdinalIgnoreCase) ||
                         entry.FullName.StartsWith(ImagesDirectoryName + "\\", StringComparison.OrdinalIgnoreCase)) &&
                        !entry.FullName.EndsWith("/") && !entry.FullName.EndsWith("\\"))
                    {
                        var fileName = Path.GetFileName(entry.FullName);
                        if (!string.IsNullOrEmpty(fileName))
                        {
                            var targetPath = Path.Combine(tempDir, ImagesDirectoryName, fileName);
                            var targetDir = Path.GetDirectoryName(targetPath);
                            if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir))
                                Directory.CreateDirectory(targetDir);

                            entry.ExtractToFile(targetPath, overwrite: true);
                            result.Add(targetPath);
                        }
                    }
                }

                result.Sort();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GmdFileService] 从.gmd提取图片失败: {ex.Message}");
            }

            return result;
        }

        public async Task UpdateGmdMetadataAsync(string gmdFilePath, Game game)
        {
            if (string.IsNullOrWhiteSpace(gmdFilePath))
                throw new ArgumentException(".gmd文件路径不能为空", nameof(gmdFilePath));
            if (game == null)
                throw new ArgumentNullException(nameof(game));
            if (!File.Exists(gmdFilePath))
                throw new FileNotFoundException(".gmd文件不存在", gmdFilePath);

            var semaphore = _fileLocks.GetOrAdd(gmdFilePath, _ => new SemaphoreSlim(1, 1));
            await semaphore.WaitAsync();
            try
            {
                var tempFilePath = gmdFilePath + ".tmp";

                DeleteTempFileIfExists(tempFilePath);

                await Task.Run(() =>
                {
                    using (var sourceArchive = ZipFile.OpenRead(gmdFilePath))
                    using (var tempArchive = ZipFile.Open(tempFilePath, ZipArchiveMode.Create))
                    {
                        foreach (var entry in sourceArchive.Entries)
                        {
                            if (entry.FullName == MetadataFileName)
                                continue;

                            var newEntry = tempArchive.CreateEntry(entry.FullName);
                            using (var sourceStream = entry.Open())
                            using (var destStream = newEntry.Open())
                            {
                                sourceStream.CopyTo(destStream);
                            }
                        }

                        var metadata = CreateMetadataFromGame(game);
                        var json = JsonSerializer.Serialize(metadata, new JsonSerializerOptions
                        {
                            WriteIndented = true,
                            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                        });

                        var metadataEntry = tempArchive.CreateEntry(MetadataFileName);
                        using (var entryStream = metadataEntry.Open())
                        using (var writer = new StreamWriter(entryStream))
                        {
                            writer.Write(json);
                        }
                    }

                    FileDeleteWithRetry(gmdFilePath);
                    File.Move(tempFilePath, gmdFilePath);

                    Debug.WriteLine($"[GmdFileService] 成功更新.gmd元数据: {gmdFilePath}");
                });
            }
            catch
            {
                var tempFilePath = gmdFilePath + ".tmp";
                DeleteTempFileIfExists(tempFilePath);
                throw;
            }
            finally
            {
                semaphore.Release();
                _fileLocks.TryRemove(gmdFilePath, out _);
            }
        }

        private static void DeleteTempFileIfExists(string path)
        {
            if (File.Exists(path))
            {
                try { File.Delete(path); }
                catch { }
            }
        }

        private static void FileDeleteWithRetry(string path, int maxRetries = 5, int delayMs = 200)
        {
            for (int i = 0; i < maxRetries; i++)
            {
                try
                {
                    File.Delete(path);
                    return;
                }
                catch (IOException) when (i < maxRetries - 1)
                {
                    Thread.Sleep(delayMs);
                }
            }
            File.Delete(path);
        }

        public GmdMetadata CreateMetadataFromGame(Game game)
        {
            var relativePath = "";
            try
            {
                var gmdDir = Path.GetDirectoryName(GetGmdFilePath(game.ExecutablePath, game.GameId));
                var winRelative = Path.GetRelativePath(gmdDir ?? "", game.ExecutablePath);
                relativePath = "./" + winRelative.Replace('\\', '/');
            }
            catch
            {
                relativePath = game.ExecutablePath ?? "";
            }

            return new GmdMetadata
            {
                Id = game.Id,
                GameId = game.GameId ?? string.Empty,
                Name = game.Name ?? string.Empty,
                ExecutablePath = relativePath,
                Description = game.Description ?? string.Empty,
                CreatedAt = game.CreatedAt,
                LaunchCount = game.LaunchCount,
                TotalPlayTime = game.TotalPlayTime,
                LastRunTime = game.LastRunTime,
                Tags = game.Tags?.ToList() ?? new List<string>(),
                Collections = game.Collections?.Select(c => c.Name).ToList() ?? new List<string>(),
                Version = 1
            };
        }

        private Game CreateGameFromMetadata(GmdMetadata metadata, string gmdFilePath)
        {
            var absoluteExePath = metadata.ExecutablePath ?? string.Empty;
            try
            {
                if (absoluteExePath.StartsWith("./") || absoluteExePath.StartsWith(".\\"))
                {
                    var gmdDir = Path.GetDirectoryName(gmdFilePath);
                    if (!string.IsNullOrEmpty(gmdDir))
                    {
                        var relativePart = absoluteExePath.Substring(2);
                        absoluteExePath = Path.GetFullPath(Path.Combine(gmdDir, relativePart.Replace('/', '\\')));
                    }
                }
            }
            catch
            {
                absoluteExePath = metadata.ExecutablePath ?? string.Empty;
            }

            var game = new Game
            {
                Id = metadata.Id,
                GameId = metadata.GameId ?? string.Empty,
                Name = metadata.Name ?? string.Empty,
                ExecutablePath = absoluteExePath,
                Description = metadata.Description ?? string.Empty,
                CreatedAt = metadata.CreatedAt,
                LaunchCount = metadata.LaunchCount,
                TotalPlayTime = metadata.TotalPlayTime,
                LastRunTime = metadata.LastRunTime
            };

            if (metadata.Tags != null)
            {
                foreach (var tag in metadata.Tags)
                {
                    game.Tags.Add(tag);
                }
            }

            if (metadata.Collections != null)
            {
                foreach (var colName in metadata.Collections)
                {
                    game.Collections.Add(new GameCollection { Name = colName });
                }
            }

            return game;
        }

        private async Task AddIconToArchiveAsync(ZipArchive archive, string iconPath)
        {
            if (string.IsNullOrWhiteSpace(iconPath) || !File.Exists(iconPath))
            {
                Debug.WriteLine("[GmdFileService] 图标文件不存在，跳过");
                return;
            }

            try
            {
                await ConvertImageToJpegAsync(iconPath, archive, IconFileName);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GmdFileService] 添加图标失败: {iconPath}, 错误: {ex.Message}");
            }
        }

        private async Task AddImagesToArchiveAsync(ZipArchive archive, System.Collections.ObjectModel.ObservableCollection<string> imagePaths)
        {
            if (imagePaths == null || imagePaths.Count == 0)
            {
                Debug.WriteLine("[GmdFileService] 没有预览图片，跳过");
                return;
            }

            int imageIndex = 1;
            foreach (var imagePath in imagePaths)
            {
                if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
                {
                    Debug.WriteLine($"[GmdFileService] 预览图片不存在，跳过: {imagePath}");
                    continue;
                }

                try
                {
                    var extension = Path.GetExtension(imagePath).ToLowerInvariant();
                    var entryName = $"{ImagesDirectoryName}/image{imageIndex}{(extension == ".gif" ? ".gif" : ".jpg")}";
                    await AddImageToArchiveAsync(archive, imagePath, entryName, extension == ".gif");
                    imageIndex++;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[GmdFileService] 添加预览图片失败: {imagePath}, 错误: {ex.Message}");
                }
            }
        }

        private async Task AddImageToArchiveAsync(ZipArchive archive, string sourceImagePath, string entryName, bool isGif)
        {
            try
            {
                if (isGif)
                {
                    await CopyImageToArchiveAsync(sourceImagePath, archive, entryName);
                }
                else
                {
                    await ConvertImageToJpegAsync(sourceImagePath, archive, entryName);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GmdFileService] 添加图片失败: {sourceImagePath}, 错误: {ex.Message}");
            }
        }

        private async Task CopyImageToArchiveAsync(string sourceImagePath, ZipArchive archive, string entryName)
        {
            var entry = archive.CreateEntry(entryName);
            using var entryStream = entry.Open();
            using var sourceStream = new FileStream(sourceImagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            await sourceStream.CopyToAsync(entryStream);
        }

        private async Task ConvertImageToJpegAsync(string sourceImagePath, ZipArchive archive, string entryName)
        {
            try
            {
                using (var stream = new FileStream(sourceImagePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var randomAccessStream = stream.AsRandomAccessStream())
                {
                    var decoder = await BitmapDecoder.CreateAsync(randomAccessStream);

                    var width = decoder.OrientedPixelWidth;
                    var height = decoder.OrientedPixelHeight;
                    double scale = 1.0;

                    const int maxWidth = 1920;
                    const int maxHeight = 1080;
                    if (width > maxWidth || height > maxHeight)
                    {
                        var scaleX = (double)maxWidth / width;
                        var scaleY = (double)maxHeight / height;
                        scale = Math.Min(scaleX, scaleY);
                    }

                    var newWidth = width;
                    var newHeight = height;
                    var transform = new BitmapTransform();
                    if (scale < 1.0)
                    {
                        transform.ScaledWidth = (uint)(width * scale);
                        transform.ScaledHeight = (uint)(height * scale);
                        newWidth = (uint)(width * scale);
                        newHeight = (uint)(height * scale);
                    }

                    var pixelData = await decoder.GetPixelDataAsync(
                        BitmapPixelFormat.Bgra8,
                        BitmapAlphaMode.Ignore,
                        transform,
                        ExifOrientationMode.IgnoreExifOrientation,
                        ColorManagementMode.ColorManageToSRgb);

                    var entry = archive.CreateEntry(entryName);

                    var ms = new MemoryStream();
                    byte[] jpegBytes;
                    var ras = ms.AsRandomAccessStream();
                    try
                    {
                        var props = new BitmapPropertySet();
                        var qualityValue = new BitmapTypedValue(0.75, Windows.Foundation.PropertyType.Single);
                        props.Add("ImageQuality", qualityValue);

                        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, ras, props);

                        encoder.SetPixelData(
                            BitmapPixelFormat.Bgra8,
                            BitmapAlphaMode.Ignore,
                            (uint)newWidth,
                            (uint)newHeight,
                            decoder.DpiX,
                            decoder.DpiY,
                            pixelData.DetachPixelData());

                        await encoder.FlushAsync();
                        jpegBytes = ms.ToArray();
                    }
                    finally
                    {
                        ras.Dispose();
                        ms.Dispose();
                    }

                    using (var entryStream = entry.Open())
                    {
                        await entryStream.WriteAsync(jpegBytes, 0, jpegBytes.Length);
                    }
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"无法将图片转换为JPEG格式: {sourceImagePath}", ex);
            }
        }

        private string? ExtractEntryToTemp(ZipArchive archive, ZipArchiveEntry entry, string tempDir, string relativePath)
        {
            try
            {
                var targetPath = Path.Combine(tempDir, relativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar));
                var targetDir = Path.GetDirectoryName(targetPath);

                if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir))
                    Directory.CreateDirectory(targetDir);

                entry.ExtractToFile(targetPath, overwrite: true);
                return targetPath;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GmdFileService] 提取文件失败: {entry.FullName}, 错误: {ex.Message}");
                return null;
            }
        }

        public async Task SyncGameToGmdAsync(Game game)
        {
            if (game == null)
                throw new ArgumentNullException(nameof(game));

            try
            {
                var gmdPath = game.GmdFilePath;
                if (string.IsNullOrEmpty(gmdPath))
                {
                    gmdPath = GetGmdFilePath(game.ExecutablePath, game.GameId);
                }

                if (File.Exists(gmdPath))
                {
                    await UpdateGmdMetadataAsync(gmdPath, game);
                }
                else
                {
                    await SerializeGameToGmdAsync(game);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GmdFileService] 同步gmd失败: {ex.Message}");
            }
        }

        public async Task<(int DeletedCount, long FreedBytes, int SkippedCount)> CleanOldGmdFilesAsync(List<Game> allGames)
        {
            int deletedCount = 0;
            long freedBytes = 0;
            int skippedCount = 0;

            var newNamingPattern = new Regex(@"^GID\d{9}\.gmd$", RegexOptions.IgnoreCase);

            var processedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            await Task.Run(() =>
            {
                foreach (var game in allGames)
                {
                    try
                    {
                        var directory = Path.GetDirectoryName(game.ExecutablePath);
                        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                            continue;

                        if (!processedDirectories.Add(directory))
                            continue;

                        var gmdFiles = Directory.GetFiles(directory, "*.gmd");
                        foreach (var gmdFile in gmdFiles)
                        {
                            try
                            {
                                var fileName = Path.GetFileName(gmdFile);
                                if (newNamingPattern.IsMatch(fileName))
                                    continue;

                                var fileInfo = new FileInfo(gmdFile);
                                var fileSize = fileInfo.Length;

                                File.Delete(gmdFile);
                                deletedCount++;
                                freedBytes += fileSize;

                                Debug.WriteLine($"[GmdFileService] 已删除旧GMD文件: {gmdFile} ({fileSize} 字节)");
                            }
                            catch (Exception ex)
                            {
                                skippedCount++;
                                Debug.WriteLine($"[GmdFileService] 无法删除文件: {gmdFile}, 错误: {ex.Message}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[GmdFileService] 处理游戏目录时出错: {game.ExecutablePath}, 错误: {ex.Message}");
                    }
                }
            });

            Debug.WriteLine($"[GmdFileService] 清理完成: 删除 {deletedCount} 个文件, 释放 {freedBytes} 字节, 跳过 {skippedCount} 个文件");
            return (deletedCount, freedBytes, skippedCount);
        }
    }

    public class GmdMetadata
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("gameId")]
        public string GameId { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("executablePath")]
        public string ExecutablePath { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;

        [JsonPropertyName("createdAt")]
        public DateTime CreatedAt { get; set; }

        [JsonPropertyName("launchCount")]
        public int LaunchCount { get; set; }

        [JsonPropertyName("totalPlayTime")]
        public long TotalPlayTime { get; set; }

        [JsonPropertyName("lastRunTime")]
        public DateTime? LastRunTime { get; set; }

        [JsonPropertyName("tags")]
        public List<string> Tags { get; set; } = new();

        [JsonPropertyName("collections")]
        public List<string> Collections { get; set; } = new();

        [JsonPropertyName("version")]
        public int Version { get; set; } = 1;
    }
}
