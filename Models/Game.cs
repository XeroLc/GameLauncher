using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media;

namespace GameLauncher.Models
{
    public class Game : INotifyPropertyChanged
    {
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

        private bool _needsGmdFallback;
        public bool NeedsGmdFallback
        {
            get => _needsGmdFallback;
            set { if (_needsGmdFallback != value) { _needsGmdFallback = value; OnPropertyChanged(nameof(NeedsGmdFallback)); } }
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
                }
            }
        }

        public void AddImagePath(string path)
        {
            if (!string.IsNullOrEmpty(path) && !_imagePaths.Contains(path))
            {
                _imagePaths.Add(path);
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