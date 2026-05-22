using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GameLauncher.Models;
using GameLauncher.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GameLauncher
{
    public sealed partial class MainWindow : Window
    {
        private readonly ObservableCollection<Game> _filteredGames;
        private string? _selectedTagFilter;
        private string _currentSortMode = "CreatedAt";
        private string? _selectedCollectionFilter;
        private DispatcherTimer? _searchDebounceTimer;
        private readonly Dictionary<string, bool> _fileExistsCache = new(StringComparer.OrdinalIgnoreCase);
        private DateTime _lastFileCacheRefresh = DateTime.MinValue;

        private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
            {
                if (_searchDebounceTimer == null)
                {
                    _searchDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
                    _searchDebounceTimer.Tick += SearchDebounceTimer_Tick;
                }
                _searchDebounceTimer.Stop();
                _searchDebounceTimer.Start();
            }
        }

        private void SearchDebounceTimer_Tick(object? sender, object e)
        {
            _searchDebounceTimer?.Stop();
            ApplyFilters();
        }

        private void TagFilterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (TagFilterComboBox == null) return;

            if (TagFilterComboBox.SelectedItem is string selectedTag)
            {
                _selectedTagFilter = selectedTag;
                ApplyFilters();
            }
        }

        private void CollectionFilterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CollectionFilterComboBox == null) return;
            if (CollectionFilterComboBox.SelectedItem is string selected)
            {
                _selectedCollectionFilter = selected;
                ApplyFilters();
            }
        }

        private async Task RefreshCollectionFilterAsync()
        {
            if (CollectionFilterComboBox == null) return;

            CollectionFilterComboBox.Items.Clear();
            CollectionFilterComboBox.Items.Add("全部游戏");

            try
            {
                var collections = await _gameService.GetAllCollectionsAsync();
                var counts = await _gameService.GetCollectionGameCountsAsync();
                foreach (var col in collections)
                {
                    var count = counts.TryGetValue(col.Id, out var c) ? c : 0;
                    CollectionFilterComboBox.Items.Add($"{col.Name} ({count})");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"刷新收藏夹筛选失败: {ex.Message}");
            }

            if (_selectedCollectionFilter != null)
            {
                var idx = -1;
                for (int i = 0; i < CollectionFilterComboBox.Items.Count; i++)
                {
                    if (CollectionFilterComboBox.Items[i] is string item &&
                        item.StartsWith(_selectedCollectionFilter.Split('(')[0].Trim()))
                    {
                        idx = i;
                        break;
                    }
                }
                CollectionFilterComboBox.SelectedIndex = idx >= 0 ? idx : 0;
            }
            else
            {
                CollectionFilterComboBox.SelectedIndex = 0;
            }
        }

        private void ApplyFilters()
        {
            if (_filteredGames == null || _games == null) return;

            if ((DateTime.UtcNow - _lastFileCacheRefresh).TotalMinutes > 5)
            {
                _fileExistsCache.Clear();
                _lastFileCacheRefresh = DateTime.UtcNow;
            }

            var settings = Models.UserSettings.Instance;
            var searchText = SearchBox?.Text?.ToLowerInvariant() ?? string.Empty;
            var hasSearch = !string.IsNullOrWhiteSpace(searchText);
            var hasTagFilter = _selectedTagFilter != null && _selectedTagFilter != "全部标签";
            var hasCollectionFilter = _selectedCollectionFilter != null && _selectedCollectionFilter != "全部游戏";
            var hideUnavailable = settings.HideUnavailableGames;

            var filtered = new List<Game>();

            foreach (var game in _games)
            {
                if (hasSearch && !game.Name.ToLowerInvariant().Contains(searchText) &&
                    !(game.Description?.ToLowerInvariant().Contains(searchText) ?? false) &&
                    !game.Tags.Any(tag => tag.ToLowerInvariant().Contains(searchText))) continue;
                if (hasTagFilter && !game.Tags.Contains(_selectedTagFilter)) continue;
                if (hasCollectionFilter)
                {
                    var collectionName = _selectedCollectionFilter!.Split('(')[0].Trim();
                    if (!game.Collections.Any(c => c.Name == collectionName)) continue;
                }
                if (hideUnavailable && !IsGameExecutableAvailable(game)) continue;
                filtered.Add(game);
            }

            filtered = SortGames(filtered).ToList();

            ApplyFilteredGamesDelta(filtered);

            UpdateEmptyState();
        }

        private void ApplyFilteredGamesDelta(List<Game> newFiltered)
        {
            var currentSet = new HashSet<Game>(_filteredGames);
            var newSet = new HashSet<Game>(newFiltered);

            for (int i = _filteredGames.Count - 1; i >= 0; i--)
            {
                if (!newSet.Contains(_filteredGames[i]))
                {
                    _filteredGames.RemoveAt(i);
                }
            }

            var currentList = _filteredGames.ToList();
            for (int i = 0; i < newFiltered.Count; i++)
            {
                if (i < currentList.Count && ReferenceEquals(currentList[i], newFiltered[i]))
                    continue;

                if (i < _filteredGames.Count)
                {
                    if (!ReferenceEquals(_filteredGames[i], newFiltered[i]))
                    {
                        var oldIndex = -1;
                        for (int j = i; j < _filteredGames.Count; j++)
                        {
                            if (ReferenceEquals(_filteredGames[j], newFiltered[i]))
                            {
                                oldIndex = j;
                                break;
                            }
                        }
                        if (oldIndex >= 0)
                        {
                            _filteredGames.Move(oldIndex, i);
                        }
                        else
                        {
                            _filteredGames.Insert(i, newFiltered[i]);
                        }
                    }
                }
                else
                {
                    _filteredGames.Add(newFiltered[i]);
                }
            }

            while (_filteredGames.Count > newFiltered.Count)
            {
                _filteredGames.RemoveAt(_filteredGames.Count - 1);
            }
        }

        private bool IsGameExecutableAvailable(Game game)
        {
            if (string.IsNullOrEmpty(game.ExecutablePath)) return false;
            if (_fileExistsCache.TryGetValue(game.ExecutablePath, out var cached))
                return cached;
            var exists = System.IO.File.Exists(game.ExecutablePath);
            _fileExistsCache[game.ExecutablePath] = exists;
            return exists;
        }

        private IEnumerable<Game> SortGames(IEnumerable<Game> games)
        {
            switch (_currentSortMode)
            {
                case "Name":
                    return games.OrderBy(g => g.Name);
                case "LaunchCount":
                    return games.OrderByDescending(g => g.LaunchCount);
                case "TotalPlayTime":
                    return games.OrderByDescending(g => g.TotalPlayTime);
                case "CreatedAt":
                    return games.OrderByDescending(g => g.CreatedAt);
                case "LastRunTime":
                    return games.OrderByDescending(g => g.LastRunTime ?? DateTime.MinValue);
                default:
                    return games.OrderByDescending(g => g.CreatedAt);
            }
        }
    }
}