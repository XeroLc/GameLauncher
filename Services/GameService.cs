using GameLauncher.Data;
using GameLauncher.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace GameLauncher.Services
{
    public class GameService
    {
        private readonly GameRepository _repository;
        private readonly CollectionRepository _collectionRepo;
        private readonly DiskScanService _diskScanService;
        private readonly GmdFileService _gmdFileService;

        public GameService(GameRepository repository, DatabaseContext dbContext)
        {
            _repository = repository;
            _collectionRepo = new CollectionRepository(dbContext);
            _diskScanService = new DiskScanService();
            _gmdFileService = new GmdFileService();
        }

        public async Task<List<Game>> GetAllGamesAsync()
        {
            return await _repository.GetAllGamesAsync();
        }

        public async Task<Game?> GetGameByIdAsync(int id)
        {
            return await _repository.GetGameByIdAsync(id);
        }

        public async Task<int> AddGameAsync(Game game)
        {
            if (string.IsNullOrWhiteSpace(game.Name))
            {
                throw new ArgumentException("游戏名称不能为空");
            }

            if (string.IsNullOrWhiteSpace(game.ExecutablePath))
            {
                throw new ArgumentException("游戏路径不能为空");
            }

            var fileExists = await Task.Run(() => System.IO.File.Exists(game.ExecutablePath));
            if (!fileExists)
            {
                throw new ArgumentException("游戏路径不存在");
            }

            return await _repository.AddGameAsync(game);
        }

        public async Task<bool> GameIdExistsAsync(string gameId)
        {
            return await _repository.GameIdExistsAsync(gameId);
        }

        public async Task<bool> UpdateGameAsync(Game game)
        {
            if (string.IsNullOrWhiteSpace(game.Name))
            {
                throw new ArgumentException("游戏名称不能为空");
            }

            if (string.IsNullOrWhiteSpace(game.ExecutablePath))
            {
                throw new ArgumentException("游戏路径不能为空");
            }

            var fileExists = await Task.Run(() => System.IO.File.Exists(game.ExecutablePath));
            if (!fileExists)
            {
                throw new ArgumentException("游戏路径不存在");
            }

            var result = await _repository.UpdateGameAsync(game);
            return result;
        }

        public async Task<bool> DeleteGameAsync(int id)
        {
            return await _repository.DeleteGameAsync(id);
        }

        public async Task<int> DeleteGamesAsync(IEnumerable<int> ids)
        {
            return await _repository.DeleteGamesAsync(ids);
        }

        public async Task<bool> LaunchGameAsync(Game game)
        {
            try
            {
                var fileExists = await Task.Run(() => System.IO.File.Exists(game.ExecutablePath));
                if (!fileExists)
                {
                    return false;
                }

                var extension = System.IO.Path.GetExtension(game.ExecutablePath).ToLowerInvariant();
                var workingDirectory = System.IO.Path.GetDirectoryName(game.ExecutablePath);
                var fileName = System.IO.Path.GetFileName(game.ExecutablePath);

                ProcessStartInfo startInfo;

                if (extension == ".bat")
                {
                    startInfo = new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = $"/c \"{fileName}\"",
                        UseShellExecute = true,
                        WorkingDirectory = workingDirectory
                    };
                }
                else
                {
                    startInfo = new ProcessStartInfo
                    {
                        FileName = game.ExecutablePath,
                        UseShellExecute = true,
                        WorkingDirectory = workingDirectory
                    };
                }

                Process.Start(startInfo);

                game.LaunchCount++;
                game.LastRunTime = DateTime.UtcNow;
                game.IsRunning = true;

                await _repository.UpdateGameAsync(game);

                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public async Task<bool> UpdateGamePlayTimeAsync(int gameId, long additionalTime)
        {
            var game = await _repository.GetGameByIdAsync(gameId);
            if (game == null)
            {
                return false;
            }

            game.TotalPlayTime += additionalTime;
            game.IsRunning = false;

            EnsureGmdFilePath(game);

            var result = await _repository.UpdateGameAsync(game);
            return result;
        }

        public async Task<bool> UpdateGameRunningStatusAsync(int gameId, bool isRunning)
        {
            var game = await _repository.GetGameByIdAsync(gameId);
            if (game == null)
            {
                return false;
            }

            game.IsRunning = isRunning;

            EnsureGmdFilePath(game);

            var result = await _repository.UpdateGameAsync(game);
            return result;
        }

        public async Task<bool> StopGameAsync(Game game, DateTime? startTime = null)
        {
            try
            {
                var processName = System.IO.Path.GetFileNameWithoutExtension(game.ExecutablePath);
                var processes = Process.GetProcessesByName(processName);

                foreach (var process in processes)
                {
                    try
                    {
                        process.Kill();
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"终止进程失败: {process.ProcessName}, 错误: {ex.Message}");
                    }
                    finally
                    {
                        process.Dispose();
                    }
                }

                game.IsRunning = false;

                if (startTime.HasValue)
                {
                    var runTime = (long)(DateTime.UtcNow - startTime.Value).TotalSeconds;
                    if (runTime > 0)
                    {
                        game.TotalPlayTime += runTime;
                    }
                }

                EnsureGmdFilePath(game);

                await _repository.UpdateGameAsync(game);

            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"停止游戏失败: {ex.Message}");
            return false;
        }
    }

        public async Task<List<GameCollection>> GetAllCollectionsAsync()
        {
            return await _collectionRepo.GetAllCollectionsAsync();
        }

        public async Task<GameCollection> AddCollectionAsync(string name)
        {
            return await _collectionRepo.AddCollectionAsync(name);
        }

        public async Task<bool> UpdateCollectionAsync(GameCollection collection)
        {
            return await _collectionRepo.UpdateCollectionAsync(collection);
        }

        public async Task<bool> DeleteCollectionAsync(int id)
        {
            return await _collectionRepo.DeleteCollectionAsync(id);
        }

        public async Task<bool> AddGameToCollectionAsync(int gameId, int collectionId)
        {
            var result = await _collectionRepo.AddGameToCollectionAsync(gameId, collectionId);
            if (result)
            {
                _ = SyncGmdAsync(gameId);
            }
            return result;
        }

        public async Task<bool> RemoveGameFromCollectionAsync(int gameId, int collectionId)
        {
            var result = await _collectionRepo.RemoveGameFromCollectionAsync(gameId, collectionId);
            if (result)
            {
                _ = SyncGmdAsync(gameId);
            }
            return result;
        }

        public async Task<List<GameCollection>> GetCollectionsForGameAsync(int gameId)
        {
            return await _collectionRepo.GetCollectionsForGameAsync(gameId);
        }

        public async Task<int> GetCollectionGameCountAsync(int collectionId)
        {
            return await _collectionRepo.GetCollectionGameCountAsync(collectionId);
        }

        public async Task<Dictionary<int, int>> GetCollectionGameCountsAsync()
        {
            return await _collectionRepo.GetCollectionGameCountsAsync();
        }

        public async Task<Dictionary<int, List<GameCollection>>> GetAllGameCollectionMappingsAsync()
        {
            return await _collectionRepo.GetAllGameCollectionMappingsAsync();
        }

        public async Task PopulateGameCollectionsAsync(List<Game> games)
        {
            if (games == null || games.Count == 0) return;
            var mappings = await GetAllGameCollectionMappingsAsync();
            foreach (var game in games)
            {
                if (mappings.TryGetValue(game.Id, out var collections))
                {
                    game.Collections.Clear();
                    foreach (var col in collections)
                    {
                        game.Collections.Add(col);
                    }
                }
            }
        }

        private void EnsureGmdFilePath(Game game)
        {
            if (string.IsNullOrEmpty(game.GmdFilePath))
            {
                try { game.GmdFilePath = _gmdFileService.GetGmdFilePath(game.ExecutablePath, game.GameId); }
                catch { }
            }
        }

        private async Task SyncGmdAsync(int gameId, Game? existingGame = null)
        {
            try
            {
                var game = existingGame ?? await _repository.GetGameByIdAsync(gameId);
                if (game == null) return;

                var gmdPath = game.GmdFilePath;
                if (string.IsNullOrEmpty(gmdPath))
                {
                    try
                    {
                        gmdPath = _gmdFileService.GetGmdFilePath(game.ExecutablePath, game.GameId);
                    }
                    catch { }
                }

                if (string.IsNullOrEmpty(gmdPath) || !System.IO.File.Exists(gmdPath))
                    return;

                var collections = await _collectionRepo.GetCollectionsForGameAsync(gameId);
                game.Collections.Clear();
                foreach (var col in collections)
                {
                    game.Collections.Add(col);
                }

                await _gmdFileService.SyncGameToGmdAsync(game);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SyncGmdAsync 失败: {ex.Message}");
            }
        }

        public async Task<ScanResult> ScanForGmdFilesAsync(IEnumerable<Game> existingGames, IProgress<ScanProgress> progress, CancellationToken ct)
        {
            return await _diskScanService.FullScanAsync(existingGames, progress, ct);
        }

        public async Task<int> ImportGamesAsync(List<Game> games)
        {
            int imported = 0;
            var allCollections = await _collectionRepo.GetAllCollectionsAsync();
            var scanService = new DiskScanService();

            foreach (var game in games)
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(game.GmdFilePath) && System.IO.File.Exists(game.GmdFilePath))
                    {
                        var imageService = new ImageService();
                        imageService.EnsureGameImageDirectory(game.GameId);

                        var (_, previewPaths) = await scanService.ExtractImagesFromGmdToLocalAsync(game.GmdFilePath, game.GameId);

                        if (previewPaths.Count > 0)
                        {
                            game.ImagePaths.Clear();
                            foreach (var path in previewPaths)
                            {
                                game.ImagePaths.Add(path);
                            }
                        }
                    }

                    var gameId = await _repository.AddGameAsync(game);

                    if (game.Collections != null && game.Collections.Count > 0)
                    {
                        foreach (var col in game.Collections.ToList())
                        {
                            if (string.IsNullOrWhiteSpace(col.Name)) continue;
                            var existing = allCollections.FirstOrDefault(c => string.Equals(c.Name, col.Name, StringComparison.OrdinalIgnoreCase));
                            int colId;
                            if (existing != null)
                            {
                                colId = existing.Id;
                            }
                            else
                            {
                                var newCol = await _collectionRepo.AddCollectionAsync(col.Name);
                                allCollections.Add(newCol);
                                colId = newCol.Id;
                            }
                            await _collectionRepo.AddGameToCollectionAsync(gameId, colId);
                        }
                    }

                    imported++;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"导入游戏失败 {game.Name}: {ex.Message}");
                }
            }
            return imported;
        }

        public async Task<string> GenerateUniqueGameIdAsync()
        {
            const int maxRetries = 5;
            for (int attempt = 0; attempt < maxRetries; attempt++)
            {
                var digits = new byte[9];
                using (var rng = RandomNumberGenerator.Create())
                {
                    rng.GetBytes(digits);
                }

                var gid = "GID";
                for (int i = 0; i < 9; i++)
                {
                    gid += (digits[i] % 10).ToString();
                }

                if (!await _repository.GameIdExistsAsync(gid))
                {
                    return gid;
                }
            }

            throw new InvalidOperationException($"无法在 {maxRetries} 次尝试内生成唯一的 GameId");
        }
    }
}