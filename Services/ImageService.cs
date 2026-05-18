using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace GameLauncher.Services
{
    public class ImageService
    {
        private readonly string _baseDirectory;

        public ImageService()
        {
            var appDataPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GameLauncher");
            _baseDirectory = Path.Combine(appDataPath, "GameLauncher_Images");
        }

        public string BaseDirectory => _baseDirectory;

        public string GetGameDirectory(string gameId)
        {
            if (string.IsNullOrWhiteSpace(gameId))
                throw new ArgumentException("游戏ID不能为空", nameof(gameId));
            return Path.Combine(_baseDirectory, gameId);
        }

        public string GetIconPath(string gameId)
        {
            return Path.Combine(GetGameDirectory(gameId), "icon.jpg");
        }

        public string GetPreviewImagePath(string gameId, int index)
        {
            return Path.Combine(GetGameDirectory(gameId), $"preview_{index}.jpg");
        }

        public List<string> GetAllPreviewImagePaths(string gameId)
        {
            var gameDir = GetGameDirectory(gameId);
            if (!Directory.Exists(gameDir))
                return new List<string>();

            var files = new List<string>();
            files.AddRange(Directory.GetFiles(gameDir, "preview_*.jpg"));
            files.AddRange(Directory.GetFiles(gameDir, "preview_*.gif"));
            return files.OrderBy(p => p).ToList();
        }

        public void EnsureGameImageDirectory(string gameId)
        {
            var gameDir = GetGameDirectory(gameId);
            if (!Directory.Exists(gameDir))
            {
                Directory.CreateDirectory(gameDir);
            }
        }

        public async Task<string?> SaveIconAsync(string gameId, string sourceIconPath)
        {
            if (string.IsNullOrWhiteSpace(gameId))
                return null;
            if (string.IsNullOrWhiteSpace(sourceIconPath) || !File.Exists(sourceIconPath))
                return null;

            try
            {
                EnsureGameImageDirectory(gameId);
                var targetPath = GetIconPath(gameId);
                await CopyAndConvertToJpegAsync(sourceIconPath, targetPath);
                return targetPath;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ImageService] 保存图标失败: {sourceIconPath}, 错误: {ex.Message}");
                return null;
            }
        }

        public async Task<string?> SavePreviewImageAsync(string gameId, string sourceImagePath, int index)
        {
            if (string.IsNullOrWhiteSpace(gameId))
                return null;
            if (string.IsNullOrWhiteSpace(sourceImagePath) || !File.Exists(sourceImagePath))
                return null;

            try
            {
                EnsureGameImageDirectory(gameId);

                var extension = Path.GetExtension(sourceImagePath).ToLowerInvariant();
                if (extension == ".gif" && await IsAnimatedGifAsync(sourceImagePath))
                {
                    var targetPath = Path.Combine(GetGameDirectory(gameId), $"preview_{index}.gif");
                    await CopyGifAsync(sourceImagePath, targetPath);
                    return targetPath;
                }
                else
                {
                    var targetPath = GetPreviewImagePath(gameId, index);
                    await CopyAndConvertToJpegAsync(sourceImagePath, targetPath);
                    return targetPath;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ImageService] 保存预览图失败: {sourceImagePath}, 错误: {ex.Message}");
                return null;
            }
        }

        public void DeleteGameImages(string gameId)
        {
            if (string.IsNullOrWhiteSpace(gameId))
                return;

            try
            {
                var gameDir = GetGameDirectory(gameId);
                if (Directory.Exists(gameDir))
                {
                    Directory.Delete(gameDir, recursive: true);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ImageService] 删除游戏图片失败: {gameId}, 错误: {ex.Message}");
            }
        }

        public void DeleteIcon(string gameId)
        {
            try
            {
                var iconPath = GetIconPath(gameId);
                if (File.Exists(iconPath))
                {
                    File.Delete(iconPath);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ImageService] 删除图标失败: {gameId}, 错误: {ex.Message}");
            }
        }

        public void DeletePreviewImage(string gameId, int index)
        {
            try
            {
                var previewPathJpg = GetPreviewImagePath(gameId, index);
                if (File.Exists(previewPathJpg))
                {
                    File.Delete(previewPathJpg);
                }

                var gameDir = GetGameDirectory(gameId);
                var previewPathGif = Path.Combine(gameDir, $"preview_{index}.gif");
                if (File.Exists(previewPathGif))
                {
                    File.Delete(previewPathGif);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ImageService] 删除预览图失败: {gameId}, 错误: {ex.Message}");
            }
        }

        private async Task<bool> IsAnimatedGifAsync(string filePath)
        {
            try
            {
                using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var randomAccessStream = stream.AsRandomAccessStream())
            {
                var decoder = await BitmapDecoder.CreateAsync(randomAccessStream);
                return decoder.FrameCount > 1;
            }
            }
            catch
            {
                return false;
            }
        }

        private async Task CopyAndConvertToJpegAsync(string sourcePath, string targetPath)
        {
            if (!File.Exists(sourcePath))
                return;

            using (var stream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
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

                var pixels = pixelData.DetachPixelData();

                var props = new BitmapPropertySet();
                var qualityValue = new BitmapTypedValue(0.75, Windows.Foundation.PropertyType.Single);
                props.Add("ImageQuality", qualityValue);

                using (var fileStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var ras = fileStream.AsRandomAccessStream())
                {
                    var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, ras, props);

                    encoder.SetPixelData(
                        BitmapPixelFormat.Bgra8,
                        BitmapAlphaMode.Ignore,
                        (uint)newWidth,
                        (uint)newHeight,
                        decoder.DpiX,
                        decoder.DpiY,
                        pixels);

                    await encoder.FlushAsync();
                }
            }
        }

        private async Task CopyGifAsync(string sourcePath, string targetPath)
        {
            if (!File.Exists(sourcePath))
                return;

            await Task.Run(() => File.Copy(sourcePath, targetPath, overwrite: true));
        }
    }
}
