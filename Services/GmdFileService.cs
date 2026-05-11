using GameLauncher.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace GameLauncher.Services
{
    public class GmdFileService
    {
        private const string MetadataFileName = "metadata.json";
        private const string IconFileName = "icon.png";
        private const string ImagesDirectoryName = "images";

        public string GetGmdFilePath(string executablePath, string gameName)
        {
            if (string.IsNullOrWhiteSpace(executablePath))
                throw new ArgumentException("可执行文件路径不能为空", nameof(executablePath));
            if (string.IsNullOrWhiteSpace(gameName))
                throw new ArgumentException("游戏名称不能为空", nameof(gameName));

            var directory = Path.GetDirectoryName(executablePath) ?? throw new InvalidOperationException("无法获取可执行文件目录");
            var sanitizedName = SanitizeFileName(gameName);
            return Path.Combine(directory, $"{sanitizedName}.gmd");
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

            var gmdFilePath = GetGmdFilePath(game.ExecutablePath, game.Name);
            var directory = Path.GetDirectoryName(gmdFilePath) ?? throw new InvalidOperationException("无法获取.gmd文件目录");

            if (!Directory.Exists(directory))
                Directory.CreateDirectory(directory);

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

        public async Task<Game> DeserializeGameFromGmdAsync(string gmdFilePath)
        {
            if (string.IsNullOrWhiteSpace(gmdFilePath))
                throw new ArgumentException(".gmd文件路径不能为空", nameof(gmdFilePath));
            if (!File.Exists(gmdFilePath))
                throw new FileNotFoundException(".gmd文件不存在", gmdFilePath);

            return await Task.Run(async () =>
            {
                var tempDir = Path.Combine(Path.GetTempPath(), "GameLauncher", "GmdExtract", Guid.NewGuid().ToString());

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

                        var iconEntry = archive.GetEntry(IconFileName);
                        if (iconEntry != null)
                        {
                            var iconPath = ExtractEntryToTemp(archive, iconEntry, tempDir, IconFileName);
                            if (!string.IsNullOrEmpty(iconPath))
                            {
                                game.IconPath = iconPath;
                            }
                        }

                        var imagePaths = new List<string>();
                        foreach (var entry in archive.Entries)
                        {
                            if (entry.FullName.StartsWith(ImagesDirectoryName + "/", StringComparison.OrdinalIgnoreCase) ||
                                entry.FullName.StartsWith(ImagesDirectoryName + "\\", StringComparison.OrdinalIgnoreCase))
                            {
                                if (!entry.FullName.EndsWith("/") && !entry.FullName.EndsWith("\\"))
                                {
                                    var fileName = Path.GetFileName(entry.FullName);
                                    if (!string.IsNullOrEmpty(fileName))
                                    {
                                        var imagePath = ExtractEntryToTemp(archive, entry, tempDir, entry.FullName);
                                        if (!string.IsNullOrEmpty(imagePath))
                                        {
                                            imagePaths.Add(imagePath);
                                        }
                                    }
                                }
                            }
                        }

                        imagePaths.Sort();
                        foreach (var path in imagePaths)
                        {
                            game.ImagePaths.Add(path);
                        }

                        game.GmdFilePath = gmdFilePath;
                        game.IsGmdFileReady = true;

                        Debug.WriteLine($"[GmdFileService] 成功从.gmd文件加载游戏: {gmdFilePath}, 图片数量: {game.ImagePaths.Count}");

                        return game;
                    }
                }
                catch
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
                    throw;
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

            await Task.Run(() =>
            {
                var tempFilePath = gmdFilePath + ".tmp";

                try
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

                    File.Delete(gmdFilePath);
                    File.Move(tempFilePath, gmdFilePath);

                    Debug.WriteLine($"[GmdFileService] 成功更新.gmd元数据: {gmdFilePath}");
                }
                catch
                {
                    if (File.Exists(tempFilePath))
                    {
                        try
                        {
                            File.Delete(tempFilePath);
                        }
                        catch
                        {
                        }
                    }
                    throw;
                }
            });
        }

        private GmdMetadata CreateMetadataFromGame(Game game)
        {
            var relativePath = "";
            try
            {
                var gmdDir = Path.GetDirectoryName(GetGmdFilePath(game.ExecutablePath, game.Name));
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
                Name = game.Name ?? string.Empty,
                ExecutablePath = relativePath,
                Description = game.Description ?? string.Empty,
                CreatedAt = game.CreatedAt,
                LaunchCount = game.LaunchCount,
                TotalPlayTime = game.TotalPlayTime,
                LastRunTime = game.LastRunTime,
                IsFavorite = game.IsFavorite,
                Tags = game.Tags?.ToList() ?? new List<string>(),
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
                Name = metadata.Name ?? string.Empty,
                ExecutablePath = absoluteExePath,
                Description = metadata.Description ?? string.Empty,
                CreatedAt = metadata.CreatedAt,
                LaunchCount = metadata.LaunchCount,
                TotalPlayTime = metadata.TotalPlayTime,
                LastRunTime = metadata.LastRunTime,
                IsFavorite = metadata.IsFavorite
            };

            if (metadata.Tags != null)
            {
                foreach (var tag in metadata.Tags)
                {
                    game.Tags.Add(tag);
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
                await AddImageToArchiveAsync(archive, iconPath, IconFileName);
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
                    var entryName = $"{ImagesDirectoryName}/image{imageIndex}.png";
                    await AddImageToArchiveAsync(archive, imagePath, entryName);
                    imageIndex++;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[GmdFileService] 添加预览图片失败: {imagePath}, 错误: {ex.Message}");
                }
            }
        }

        private async Task AddImageToArchiveAsync(ZipArchive archive, string sourceImagePath, string entryName)
        {
            try
            {
                await ConvertImageToPngAsync(sourceImagePath, archive, entryName);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GmdFileService] 添加图片失败: {sourceImagePath}, 错误: {ex.Message}");
            }
        }

        private async Task ConvertImageToPngAsync(string sourceImagePath, ZipArchive archive, string entryName)
        {
            try
            {
                var imageBytes = await File.ReadAllBytesAsync(sourceImagePath);

                var extension = Path.GetExtension(sourceImagePath).ToLowerInvariant();

                if (extension == ".png")
                {
                    var entry = archive.CreateEntry(entryName);
                    using (var entryStream = entry.Open())
                    {
                        await entryStream.WriteAsync(imageBytes, 0, imageBytes.Length);
                    }
                }
                else
                {
                    using (var stream = new FileStream(sourceImagePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        var randomAccessStream = stream.AsRandomAccessStream();
                        var decoder = await BitmapDecoder.CreateAsync(randomAccessStream);
                        var pixelData = await decoder.GetPixelDataAsync();

                        var entry = archive.CreateEntry(entryName);

                        var ms = new MemoryStream();
                        var ras = ms.AsRandomAccessStream();
                        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, ras);

                        encoder.SetPixelData(
                            decoder.BitmapPixelFormat,
                            decoder.BitmapAlphaMode,
                            decoder.OrientedPixelWidth,
                            decoder.OrientedPixelHeight,
                            decoder.DpiX,
                            decoder.DpiY,
                            pixelData.DetachPixelData());

                        await encoder.FlushAsync();

                        using (var entryStream = entry.Open())
                        {
                            ms.Seek(0, SeekOrigin.Begin);
                            await ms.CopyToAsync(entryStream);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"无法将图片转换为PNG格式: {sourceImagePath}", ex);
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

        private string SanitizeFileName(string fileName)
        {
            var invalidChars = Path.GetInvalidFileNameChars();
            var sanitized = new string(fileName.ToCharArray()
                .Where(c => !invalidChars.Contains(c))
                .ToArray());

            if (string.IsNullOrWhiteSpace(sanitized))
                throw new ArgumentException("游戏名称包含无效字符，无法生成文件名");

            return sanitized;
        }
    }

    public class GmdMetadata
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

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

        [JsonPropertyName("isFavorite")]
        public bool IsFavorite { get; set; }

        [JsonPropertyName("tags")]
        public List<string> Tags { get; set; } = new();

        [JsonPropertyName("version")]
        public int Version { get; set; } = 1;
    }
}
