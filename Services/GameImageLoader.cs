using System;
using System.IO;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media.Imaging;
using GameLauncher.Models;

namespace GameLauncher.Services;

public class GameImageLoader
{
    private readonly ImageService _imageService;
    private readonly GmdFileService _gmdFileService;

    public GameImageLoader(ImageService imageService, GmdFileService gmdFileService)
    {
        _imageService = imageService;
        _gmdFileService = gmdFileService;
    }

    public void LoadIcon(Game game)
    {
        if (!string.IsNullOrEmpty(game.IconPath) && File.Exists(game.IconPath))
        {
            try
            {
                var bitmapImage = new BitmapImage();
                bitmapImage.UriSource = new Uri(game.IconPath);
                bitmapImage.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                game.IconSource = bitmapImage;
                return;
            }
            catch { }
        }

        if (!string.IsNullOrEmpty(game.GameId))
        {
            var globalIconPath = _imageService.GetIconPath(game.GameId);
            if (File.Exists(globalIconPath))
            {
                game.IconPath = globalIconPath;
                var bitmapImage = new BitmapImage();
                bitmapImage.UriSource = new Uri(globalIconPath);
                bitmapImage.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                game.IconSource = bitmapImage;
                return;
            }
        }

        game.IconSource = null;
        _ = LoadIconFromGmdAsync(game);
    }

    private async Task LoadIconFromGmdAsync(Game game)
    {
        if (string.IsNullOrEmpty(game.GmdFilePath) || !File.Exists(game.GmdFilePath))
            return;
        if (string.IsNullOrEmpty(game.GameId))
            return;

        try
        {
            var iconPath = _imageService.GetIconPath(game.GameId);

            if (!File.Exists(iconPath))
            {
                var tempIconPath = GmdFileService.ExtractIconFromGmd(game.GmdFilePath);
                if (!string.IsNullOrEmpty(tempIconPath) && File.Exists(tempIconPath))
                {
                    iconPath = await _imageService.SaveIconAsync(game.GameId, tempIconPath);
                }
            }

            if (!string.IsNullOrEmpty(iconPath) && File.Exists(iconPath))
            {
                game.IconPath = iconPath;
                var bitmapImage = new BitmapImage();
                bitmapImage.UriSource = new Uri(iconPath);
                game.IconSource = bitmapImage;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"异步加载图标失败: {ex.Message}");
        }
    }

    public void ReloadIcon(Game game)
    {
        if (!string.IsNullOrEmpty(game.IconPath) && File.Exists(game.IconPath))
        {
            try
            {
                var bitmapImage = new BitmapImage();
                bitmapImage.UriSource = new Uri(game.IconPath);
                bitmapImage.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                game.IconSource = bitmapImage;
                return;
            }
            catch { }
        }

        if (!string.IsNullOrEmpty(game.GameId))
        {
            var globalIconPath = _imageService.GetIconPath(game.GameId);
            if (File.Exists(globalIconPath))
            {
                game.IconPath = globalIconPath;
                var bitmapImage = new BitmapImage();
                bitmapImage.UriSource = new Uri(globalIconPath);
                bitmapImage.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                game.IconSource = bitmapImage;
                return;
            }
        }

        game.IconSource = null;
    }

    public void LoadImages(Game game)
    {
        game.ImageSources.Clear();

        if (!string.IsNullOrEmpty(game.GameId))
        {
            var globalPreviewPaths = _imageService.GetAllPreviewImagePaths(game.GameId);
            if (globalPreviewPaths.Count > 0)
            {
                bool needsPathSync = game.ImagePaths.Count != globalPreviewPaths.Count ||
                    !game.ImagePaths.All(p => globalPreviewPaths.Contains(p, StringComparer.OrdinalIgnoreCase));

                if (needsPathSync)
                {
                    game.ImagePaths.Clear();
                    foreach (var imgPath in globalPreviewPaths)
                    {
                        game.ImagePaths.Add(imgPath);
                    }
                }

                foreach (var imgPath in globalPreviewPaths)
                {
                    if (File.Exists(imgPath))
                    {
                        var bitmapImage = new BitmapImage();
                        bitmapImage.UriSource = new Uri(imgPath);
                        game.ImageSources.Add(bitmapImage);
                    }
                }
                return;
            }
        }

        if (game.ImagePaths.Count > 0)
        {
            bool allExist = true;
            foreach (var imgPath in game.ImagePaths)
            {
                if (!File.Exists(imgPath)) { allExist = false; break; }
            }

            if (allExist)
            {
                foreach (var imgPath in game.ImagePaths)
                {
                    if (File.Exists(imgPath))
                    {
                        var bitmapImage = new BitmapImage();
                        bitmapImage.UriSource = new Uri(imgPath);
                        game.ImageSources.Add(bitmapImage);
                    }
                }
                return;
            }
        }

        _ = LoadImagesFromGmdAsync(game);
    }

    private async Task LoadImagesFromGmdAsync(Game game)
    {
        if (string.IsNullOrEmpty(game.GmdFilePath) || !File.Exists(game.GmdFilePath))
            return;
        if (string.IsNullOrEmpty(game.GameId))
            return;

        try
        {
            var previewFiles = _imageService.GetAllPreviewImagePaths(game.GameId);

            if (previewFiles.Count == 0)
            {
                var tempImagePaths = GmdFileService.ExtractImagesFromGmd(game.GmdFilePath);
                int index = 1;
                foreach (var tempPath in tempImagePaths)
                {
                    if (File.Exists(tempPath))
                    {
                        var savedPath = await _imageService.SavePreviewImageAsync(game.GameId, tempPath, index);
                        if (!string.IsNullOrEmpty(savedPath))
                        {
                            game.ImagePaths.Add(savedPath);
                            var bitmapImage = new BitmapImage();
                            bitmapImage.UriSource = new Uri(savedPath);
                            game.ImageSources.Add(bitmapImage);
                        }
                        index++;
                    }
                }
            }
            else
            {
                game.ImagePaths.Clear();
                foreach (var imgPath in previewFiles)
                {
                    if (File.Exists(imgPath))
                    {
                        game.ImagePaths.Add(imgPath);
                        var bitmapImage = new BitmapImage();
                        bitmapImage.UriSource = new Uri(imgPath);
                        game.ImageSources.Add(bitmapImage);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"异步加载预览图失败: {ex.Message}");
        }
    }

    public void ReloadImages(Game game)
    {
        game.ImageSources.Clear();

        if (!string.IsNullOrEmpty(game.GameId))
        {
            var globalPreviewPaths = _imageService.GetAllPreviewImagePaths(game.GameId);
            if (globalPreviewPaths.Count > 0)
            {
                bool needsPathSync = game.ImagePaths.Count != globalPreviewPaths.Count ||
                    !game.ImagePaths.All(p => globalPreviewPaths.Contains(p, StringComparer.OrdinalIgnoreCase));

                if (needsPathSync)
                {
                    game.ImagePaths.Clear();
                    foreach (var imgPath in globalPreviewPaths)
                    {
                        game.ImagePaths.Add(imgPath);
                    }
                }

                foreach (var imgPath in globalPreviewPaths)
                {
                    if (File.Exists(imgPath))
                    {
                        try
                        {
                            var bitmapImage = new BitmapImage();
                            bitmapImage.UriSource = new Uri(imgPath);
                            bitmapImage.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                            game.ImageSources.Add(bitmapImage);
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"加载图片失败: {imgPath}, 错误: {ex.Message}");
                        }
                    }
                }
                return;
            }
        }

        foreach (var imagePath in game.ImagePaths)
        {
            if (!string.IsNullOrEmpty(imagePath) && File.Exists(imagePath))
            {
                try
                {
                    var bitmapImage = new BitmapImage();
                    bitmapImage.UriSource = new Uri(imagePath);
                    bitmapImage.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                    game.ImageSources.Add(bitmapImage);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"加载图片失败: {imagePath}, 错误: {ex.Message}");
                }
            }
        }
    }
}
