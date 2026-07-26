using GameLauncher.Models;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace GameLauncher.Services
{
    public class PrivateModeService : INotifyPropertyChanged
    {
        private static PrivateModeService? _instance;
        private static readonly object _lock = new object();

        private bool _isPrivateMode;
        private List<int> _keySequence = new List<int>();
        private List<int> _inputBuffer = new List<int>();

        private static readonly List<int> DefaultKeySequence = new List<int>
        {
            38, 38, 40, 40, 37, 37, 39, 39, 65, 65, 66, 66
        };

        public event PropertyChangedEventHandler? PropertyChanged;

        public static PrivateModeService Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        _instance ??= new PrivateModeService();
                    }
                }
                return _instance;
            }
        }

        public bool IsPrivateMode
        {
            get => _isPrivateMode;
            set
            {
                if (_isPrivateMode != value)
                {
                    _isPrivateMode = value;
                    OnPropertyChanged();
                }
            }
        }

        public List<int> KeySequence
        {
            get => _keySequence;
            private set
            {
                _keySequence = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasPassword));
            }
        }

        public List<int> InputBuffer
        {
            get => _inputBuffer;
            private set
            {
                _inputBuffer = value;
                OnPropertyChanged();
            }
        }

        public bool HasPassword =>
            UserSettings.Instance.PrivateKeySequence != null &&
            UserSettings.Instance.PrivateKeySequence.Count > 0;

        private PrivateModeService()
        {
            LoadKeySequence();
        }

        private void LoadKeySequence()
        {
            var saved = UserSettings.Instance.PrivateKeySequence;
            if (saved != null && saved.Count > 0)
            {
                _keySequence = new List<int>(saved);
            }
            else
            {
                _keySequence = new List<int>(DefaultKeySequence);
            }
        }

        public void ReloadFromSettings()
        {
            LoadKeySequence();
            OnPropertyChanged(nameof(HasPassword));
        }

        public void RecordKey(int virtualKey)
        {
            _inputBuffer.Add(virtualKey);

            if (CheckMatch())
            {
                TogglePrivateMode();
                _inputBuffer.Clear();
                OnPropertyChanged(nameof(InputBuffer));
                return;
            }

            if (_inputBuffer.Count > _keySequence.Count)
            {
                int removeCount = _inputBuffer.Count - _keySequence.Count;
                _inputBuffer.RemoveRange(0, removeCount);
                OnPropertyChanged(nameof(InputBuffer));
            }
        }

        public bool CheckMatch()
        {
            if (_keySequence.Count == 0 || _inputBuffer.Count < _keySequence.Count)
                return false;

            int startIndex = _inputBuffer.Count - _keySequence.Count;
            for (int i = 0; i < _keySequence.Count; i++)
            {
                if (_inputBuffer[startIndex + i] != _keySequence[i])
                    return false;
            }
            return true;
        }

        public void TogglePrivateMode()
        {
            IsPrivateMode = !IsPrivateMode;
            _inputBuffer.Clear();
            OnPropertyChanged(nameof(InputBuffer));
        }

        public void SetKeySequence(List<int> newSequence)
        {
            KeySequence = new List<int>(newSequence);
            UserSettings.Instance.PrivateKeySequence = new List<int>(newSequence);
            UserSettings.Instance.Save();
        }

        public void ResetToDefault()
        {
            KeySequence = new List<int>(DefaultKeySequence);
            UserSettings.Instance.PrivateKeySequence = new List<int>(DefaultKeySequence);
            UserSettings.Instance.Save();
        }

        public void ClearInputBuffer()
        {
            _inputBuffer.Clear();
            OnPropertyChanged(nameof(InputBuffer));
        }

        public static string KeyCodeToDisplayString(int virtualKey)
        {
            return virtualKey switch
            {
                8 => "Backspace",
                9 => "Tab",
                13 => "Enter",
                16 => "Shift",
                17 => "Ctrl",
                18 => "Alt",
                19 => "Pause",
                20 => "CapsLock",
                27 => "Esc",
                32 => "Space",
                33 => "PgUp",
                34 => "PgDn",
                35 => "End",
                36 => "Home",
                37 => "←",
                38 => "↑",
                39 => "→",
                40 => "↓",
                44 => "PrtSc",
                45 => "Insert",
                46 => "Delete",
                91 => "Win",
                92 => "Win",
                93 => "Menu",
                106 => "N*",
                107 => "N+",
                109 => "N-",
                110 => "N.",
                111 => "N/",
                144 => "NumLock",
                145 => "ScrollLock",
                160 => "LShift",
                161 => "RShift",
                162 => "LCtrl",
                163 => "RCtrl",
                164 => "LAlt",
                165 => "RAlt",
                186 => ";",
                187 => "=",
                188 => ",",
                189 => "-",
                190 => ".",
                191 => "/",
                192 => "`",
                219 => "[",
                220 => "\\",
                221 => "]",
                222 => "'",
                >= 124 and <= 135 => $"F{virtualKey - 111}",
                >= 112 and <= 123 => $"F{virtualKey - 111}",
                >= 96 and <= 105 => $"N{virtualKey - 96}",
                >= 65 and <= 90 => ((char)virtualKey).ToString().ToLower(),
                >= 48 and <= 57 => ((char)virtualKey).ToString(),
                _ => virtualKey.ToString()
            };
        }

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}