using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GameLauncher.Models;
using GameLauncher.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace GameLauncher
{
    public sealed partial class MainWindow : Window
    {
        private void GamesGridView_DragOver(object sender, DragEventArgs e)
        {
            if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                e.AcceptedOperation = DataPackageOperation.Copy;
                e.DragUIOverride.Caption = "释放以添加游戏";
            }
            else
            {
                e.AcceptedOperation = DataPackageOperation.None;
            }
        }

        private async void GamesGridView_Drop(object sender, DragEventArgs e)
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                return;
            }

            try
            {
                var items = await e.DataView.GetStorageItemsAsync();
                if (items == null || items.Count == 0u)
                {
                    return;
                }

                foreach (var item in items)
                {
                    if (item is IStorageFile file)
                    {
                        var extension = Path.GetExtension(file.Path).ToLowerInvariant();
                        
                        if (extension == ".gmd")
                        {
                            await AddGameFromGmdDragDrop(file.Path);
                        }
                        else if (extension == ".exe" || extension == ".bat" || extension == ".lnk")
                        {
                            await AddGameFromDragDrop(file.Path);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"拖放添加游戏时出错: {ex.Message}");
                await ShowErrorDialog("错误", $"添加游戏时发生错误：{ex.Message}");
            }
        }

        private async System.Threading.Tasks.Task AddGameFromDragDrop(string filePath)
        {
            if (_isDialogOpen)
            {
                return;
            }

            try
            {
                _isDialogOpen = true;
                
                var existingGame = _games.FirstOrDefault(g => 
                    string.Equals(g.ExecutablePath, filePath, StringComparison.OrdinalIgnoreCase));
                
                if (existingGame != null)
                {
                    var infoDialog = new ContentDialog
                    {
                        Title = "游戏已存在",
                        Content = $"游戏「{existingGame.Name}」已经存在于库中",
                        CloseButtonText = "确定",
                        XamlRoot = Content.XamlRoot,
                        Style = (Style)App.Current.Resources["DefaultContentDialogStyle"]
                    };
                    await infoDialog.ShowAsync();
                    return;
                }

                var fileName = Path.GetFileNameWithoutExtension(filePath);
                var newGame = new Game
                {
                    Name = fileName,
                    ExecutablePath = filePath,
                    IconPath = string.Empty,
                    Description = string.Empty
                };

                _gameImageLoader.LoadIcon(newGame);
                _gameImageLoader.LoadImages(newGame);

                try
                {
                    await _gameService.AddGameAsync(newGame);
                    await SilentRefreshGamesAsync(forceUiUpdate: true);
                    
                    var successDialog = new ContentDialog
                    {
                        Title = "添加成功",
                        Content = $"已成功添加游戏「{fileName}」",
                        CloseButtonText = "确定",
                        XamlRoot = Content.XamlRoot,
                        Style = (Style)App.Current.Resources["DefaultContentDialogStyle"]
                    };
                    await successDialog.ShowAsync();
                }
                catch (Exception ex)
                {
                    await ShowErrorDialog("添加游戏失败", ex.Message);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"从拖放添加游戏时出错: {ex.Message}");
                await ShowErrorDialog("错误", $"添加游戏时发生错误：{ex.Message}");
            }
            finally
            {
                _isDialogOpen = false;
            }
        }

        private async System.Threading.Tasks.Task AddGameFromGmdDragDrop(string gmdFilePath)
        {
            if (_isDialogOpen) return;

            try
            {
                _isDialogOpen = true;

                if (!System.IO.File.Exists(gmdFilePath))
                {
                    await ShowErrorDialog("文件不存在", $"文件「{gmdFilePath}」不存在。");
                    return;
                }

                Game importedGame;
                try
                {
                    importedGame = await _gmdService.DeserializeGameFromGmdAsync(gmdFilePath);
                }
                catch (Exception ex)
                {
                    await ShowErrorDialog("导入失败", $"无法解析 .gmd 文件：{ex.Message}");
                    return;
                }

                if (!string.IsNullOrWhiteSpace(importedGame.GameId) && await _gameService.GameIdExistsAsync(importedGame.GameId))
                {
                    await ShowErrorDialog("提示", $"游戏「{importedGame.Name}」已存在于数据库中，无需重复添加。");
                    return;
                }

                LoadingOverlay.Visibility = Visibility.Visible;
                try
                {
                    var imageService = _imageService;
                    imageService.EnsureGameImageDirectory(importedGame.GameId);

                    var gameId = await _gameService.AddGameAsync(importedGame);

                    if (importedGame.Collections != null && importedGame.Collections.Count > 0)
                    {
                        var allCollections = await _gameService.GetAllCollectionsAsync();
                        foreach (var col in importedGame.Collections.ToList())
                        {
                            if (string.IsNullOrWhiteSpace(col.Name)) continue;
                            int colId;
                            var existing = allCollections.FirstOrDefault(c => string.Equals(c.Name, col.Name, StringComparison.OrdinalIgnoreCase));
                            if (existing != null)
                            {
                                colId = existing.Id;
                            }
                            else
                            {
                                var newCol = await _gameService.AddCollectionAsync(col.Name);
                                allCollections.Add(newCol);
                                colId = newCol.Id;
                            }
                            await _gameService.AddGameToCollectionAsync(gameId, colId);
                        }
                    }

                    _gameImageLoader.ReloadIcon(importedGame);
                    _gameImageLoader.ReloadImages(importedGame);

                    LoadingOverlay.Visibility = Visibility.Collapsed;
                    await SilentRefreshGamesAsync(forceUiUpdate: true);

                    var successDialog = new ContentDialog
                    {
                        Title = "导入成功",
                        Content = $"已成功导入游戏「{importedGame.Name}」",
                        CloseButtonText = "确定",
                        XamlRoot = Content.XamlRoot,
                        Style = (Style)App.Current.Resources["DefaultContentDialogStyle"]
                    };
                    await successDialog.ShowAsync();
                }
                catch (Exception ex)
                {
                    LoadingOverlay.Visibility = Visibility.Collapsed;
                    await ShowErrorDialog("导入失败", ex.Message);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"从.gmd拖放导入时出错: {ex.Message}");
                await ShowErrorDialog("错误", $"导入游戏时发生错误：{ex.Message}");
            }
            finally
            {
                _isDialogOpen = false;
            }
        }
    }
}