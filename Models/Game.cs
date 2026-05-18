using GameLauncher.Services;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace GameLauncher.Models
{
    public class Game : INotifyPropertyChanged
    {
        private static readonly Services.ImageService _sharedImageService = new();

        private int _id;
        private string _name = string.Empty;
        private string _executablePath = string.Empty;
        private string _iconPath = string.Empty;
        private string _gmdFilePath = string.Empty;
        private string _description = string.Empty;
        private DateTime _createdAt;
        private int _launchCount = 0;
        private long _totalPlayTime = 0;
        private DateTime? _lastRunTime;
        private bool _isRunning = false;
        private bool _isGmdFileReady = false;
        private ImageSource? _iconSource;
        private ObservableCollection<string> _imagePaths = new ObservableCollection<string>();
        private ObservableCollection<ImageSource> _imageSources = new ObservableCollection<ImageSource>();
        private ObservableCollection<string> _tags = new ObservableCollection<string>();
        private ObservableCollection<GameCollection> _collections = new ObservableCollection<GameCollection>();

        public int Id
        {
            get => _id;
            set
            {
                if (_id != value)
                {
                    _id = value;
                    OnPropertyChanged(nameof(Id));
                }
            }
        }

        private string _gameId = string.Empty;

        public string GameId
        {
            get => _gameId;
            set
            {
                if (_gameId != value)
                {
                    _gameId = value;
                    OnPropertyChanged(nameof(GameId));
                }
            }
        }

        public string Name
        {
            get => _name;
            set
            {
                if (_name != value)
                {
                    _name = value;
                    OnPropertyChanged(nameof(Name));
                }
            }
        }

        public string ExecutablePath
        {
            get => _executablePath;
            set
            {
                if (_executablePath != value)
                {
                    _executablePath = value;
                    OnPropertyChanged(nameof(ExecutablePath));
                }
            }
        }

        public string IconPath
        {
            get => _iconPath;
            set
            {
                if (_iconPath != value)
                {
                    _iconPath = value;
                    OnPropertyChanged(nameof(IconPath));
                }
            }
        }

        public string GmdFilePath
        {
            get => _gmdFilePath;
            set
            {
                if (_gmdFilePath != value)
                {
                    _gmdFilePath = value;
                    OnPropertyChanged(nameof(GmdFilePath));
                }
            }
        }

        public ImageSource? IconSource
        {
            get => _iconSource;
            set
            {
                if (_iconSource != value)
                {
                    _iconSource = value;
                    OnPropertyChanged(nameof(IconSource));
                }
            }
        }

        public void LoadIcon()
        {
            if (!string.IsNullOrEmpty(_iconPath) && System.IO.File.Exists(_iconPath))
            {
                try
                {
                    var bitmapImage = new BitmapImage();
                    bitmapImage.UriSource = new Uri(_iconPath);
                    IconSource = bitmapImage;
                    return;
                }
                catch { }
            }

            if (!string.IsNullOrEmpty(_gmdFilePath) && System.IO.File.Exists(_gmdFilePath) && !string.IsNullOrEmpty(GameId))
            {
                try
                {
                    var iconPath = _sharedImageService.GetIconPath(GameId);

                    if (!System.IO.File.Exists(iconPath))
                    {
                        var tempIconPath = Services.GmdFileService.ExtractIconFromGmd(_gmdFilePath);
                        if (!string.IsNullOrEmpty(tempIconPath) && System.IO.File.Exists(tempIconPath))
                        {
                            iconPath = _sharedImageService.SaveIconAsync(GameId, tempIconPath).GetAwaiter().GetResult();
                        }
                    }

                    if (!string.IsNullOrEmpty(iconPath) && System.IO.File.Exists(iconPath))
                    {
                        _iconPath = iconPath;
                        var bitmapImage = new BitmapImage();
                        bitmapImage.UriSource = new Uri(iconPath);
                        IconSource = bitmapImage;
                        return;
                    }
                }
                catch { }
            }

            IconSource = null;
        }

        public void ReloadIcon()
        {
            if (!string.IsNullOrEmpty(_iconPath) && System.IO.File.Exists(_iconPath))
            {
                try
                {
                    var bitmapImage = new BitmapImage();
                    bitmapImage.UriSource = new Uri(_iconPath);
                    bitmapImage.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                    IconSource = bitmapImage;
                    return;
                }
                catch { }
            }
            IconSource = null;
        }

        public string Description
        {
            get => _description;
            set
            {
                if (_description != value)
                {
                    _description = value;
                    OnPropertyChanged(nameof(Description));
                }
            }
        }

        public DateTime CreatedAt
        {
            get => _createdAt;
            set
            {
                if (_createdAt != value)
                {
                    _createdAt = value;
                    OnPropertyChanged(nameof(CreatedAt));
                }
            }
        }

        public int LaunchCount
        {
            get => _launchCount;
            set
            {
                if (_launchCount != value)
                {
                    _launchCount = value;
                    OnPropertyChanged(nameof(LaunchCount));
                }
            }
        }

        public long TotalPlayTime
        {
            get => _totalPlayTime;
            set
            {
                if (_totalPlayTime != value)
                {
                    _totalPlayTime = value;
                    OnPropertyChanged(nameof(TotalPlayTime));
                }
            }
        }

        public DateTime? LastRunTime
        {
            get => _lastRunTime;
            set
            {
                if (_lastRunTime != value)
                {
                    _lastRunTime = value;
                    OnPropertyChanged(nameof(LastRunTime));
                }
            }
        }

        public bool IsRunning
        {
            get => _isRunning;
            set
            {
                if (_isRunning != value)
                {
                    _isRunning = value;
                    OnPropertyChanged(nameof(IsRunning));
                }
            }
        }

        public ObservableCollection<GameCollection> Collections
        {
            get => _collections;
            set
            {
                if (_collections != value)
                {
                    _collections = value ?? new ObservableCollection<GameCollection>();
                    OnPropertyChanged(nameof(Collections));
                }
            }
        }

        public bool IsInCollection(int collectionId)
        {
            return _collections.Any(c => c.Id == collectionId);
        }

        public void AddToCollection(GameCollection collection)
        {
            if (collection != null && !_collections.Any(c => c.Id == collection.Id))
            {
                _collections.Add(collection);
            }
        }

        public void RemoveFromCollection(GameCollection collection)
        {
            if (collection != null)
            {
                var existing = _collections.FirstOrDefault(c => c.Id == collection.Id);
                if (existing != null)
                {
                    _collections.Remove(existing);
                }
            }
        }

        public bool IsGmdFileReady
        {
            get => _isGmdFileReady;
            set
            {
                if (_isGmdFileReady != value)
                {
                    _isGmdFileReady = value;
                    OnPropertyChanged(nameof(IsGmdFileReady));
                }
            }
        }

        public ObservableCollection<string> ImagePaths
        {
            get => _imagePaths;
            set
            {
                if (_imagePaths != value)
                {
                    _imagePaths = value ?? new ObservableCollection<string>();
                    OnPropertyChanged(nameof(ImagePaths));
                    LoadImages();
                }
            }
        }

        public void AddImagePath(string path)
        {
            if (!string.IsNullOrEmpty(path) && !_imagePaths.Contains(path))
            {
                _imagePaths.Add(path);
                LoadImages();
            }
        }

        public ObservableCollection<ImageSource> ImageSources
        {
            get => _imageSources;
            set
            {
                if (_imageSources != value)
                {
                    _imageSources = value ?? new ObservableCollection<ImageSource>();
                    OnPropertyChanged(nameof(ImageSources));
                }
            }
        }

        public ObservableCollection<string> Tags
        {
            get => _tags;
            set
            {
                if (_tags != value)
                {
                    _tags = value ?? new ObservableCollection<string>();
                    OnPropertyChanged(nameof(Tags));
                }
            }
        }

        public void LoadImages()
        {
            _imageSources.Clear();
            var hasValidImages = false;

            foreach (var imagePath in _imagePaths)
            {
                if (!string.IsNullOrEmpty(imagePath) && System.IO.File.Exists(imagePath))
                {
                    try
                    {
                        var bitmapImage = new BitmapImage();
                        bitmapImage.UriSource = new Uri(imagePath);
                        _imageSources.Add(bitmapImage);
                        hasValidImages = true;
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"加载图片失败: {imagePath}, 错误: {ex.Message}");
                    }
                }
            }

            if (!hasValidImages && !string.IsNullOrEmpty(_gmdFilePath) && System.IO.File.Exists(_gmdFilePath) && !string.IsNullOrEmpty(GameId))
            {
                try
                {
                    var previewFiles = _sharedImageService.GetAllPreviewImagePaths(GameId);

                    if (previewFiles.Count == 0)
                    {
                        var tempImagePaths = Services.GmdFileService.ExtractImagesFromGmd(_gmdFilePath);
                        int index = 1;
                        foreach (var tempPath in tempImagePaths)
                        {
                            if (System.IO.File.Exists(tempPath))
                            {
                                var savedPath = _sharedImageService.SavePreviewImageAsync(GameId, tempPath, index).GetAwaiter().GetResult();
                                if (!string.IsNullOrEmpty(savedPath))
                                {
                                    _imagePaths.Add(savedPath);
                                    var bitmapImage = new BitmapImage();
                                    bitmapImage.UriSource = new Uri(savedPath);
                                    _imageSources.Add(bitmapImage);
                                }
                                index++;
                            }
                        }
                    }
                    else
                    {
                        foreach (var imgPath in previewFiles)
                        {
                            if (System.IO.File.Exists(imgPath))
                            {
                                _imagePaths.Add(imgPath);
                                var bitmapImage = new BitmapImage();
                                bitmapImage.UriSource = new Uri(imgPath);
                                _imageSources.Add(bitmapImage);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"从.gmd加载图片失败: {ex.Message}");
                }
            }
        }

        public void ReloadImages()
        {
            _imageSources.Clear();
            foreach (var imagePath in _imagePaths)
            {
                if (!string.IsNullOrEmpty(imagePath) && System.IO.File.Exists(imagePath))
                {
                    try
                    {
                        var bitmapImage = new BitmapImage();
                        bitmapImage.UriSource = new Uri(imagePath);
                        bitmapImage.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                        _imageSources.Add(bitmapImage);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"加载图片失败: {imagePath}, 错误: {ex.Message}");
                    }
                }
            }
        }

        public static string FormatPlayTime(long totalSeconds)
        {
            if (totalSeconds < 60) return $"{totalSeconds}秒";
            if (totalSeconds < 3600) return $"{totalSeconds / 60}分钟";
            var hours = totalSeconds / 3600;
            return hours >= 24 ? $"{hours / 24}天" : $"{hours}小时";
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}