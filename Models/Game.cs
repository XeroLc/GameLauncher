using System;
using System.ComponentModel;

namespace GameLauncher.Models
{
    public class Game : INotifyPropertyChanged
    {
        private int _id;
        private string _name = string.Empty;
        private string _executablePath = string.Empty;
        private string _iconPath = string.Empty;
        private string _description = string.Empty;
        private DateTime _createdAt;
        private int _launchCount = 0;
        private long _totalPlayTime = 0;
        private DateTime? _lastRunTime;
        private bool _isRunning = false;

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

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}