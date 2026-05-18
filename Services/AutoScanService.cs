using GameLauncher.Data;
using GameLauncher.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace GameLauncher.Services
{
    public class AutoScanResult
    {
        public int NewGamesFound { get; set; }
        public List<string> NewGameNames { get; set; } = new List<string>();
        public int TotalScanned { get; set; }
        public List<int> ImportedGameIds { get; set; } = new List<int>();
        public List<Game> DiscoveredGames { get; set; } = new List<Game>();
    }

    public class AutoScanService
    {
        private readonly GameService _gameService;
        private readonly GmdFileService _gmdFileService;

        public AutoScanService(GameService gameService)
        {
            _gameService = gameService;
            _gmdFileService = new GmdFileService();
        }

        public async Task<AutoScanResult> ScanAsync(List<string> scanPaths, CancellationToken cancellationToken = default)
        {
            var result = new AutoScanResult();

            if (scanPaths == null || scanPaths.Count == 0)
                return result;

            var allExistingGames = await _gameService.GetAllGamesAsync();
            var existingExePaths = new HashSet<string>(
                allExistingGames.Select(g => g.ExecutablePath),
                StringComparer.OrdinalIgnoreCase);

            var existingGameNames = new HashSet<string>(
                allExistingGames.Select(g => g.Name),
                StringComparer.OrdinalIgnoreCase);

            var existingGameIds = new HashSet<string>(
                allExistingGames.Where(g => !string.IsNullOrWhiteSpace(g.GameId)).Select(g => g.GameId),
                StringComparer.OrdinalIgnoreCase);

            var foundGmdFiles = new List<string>();

            foreach (var scanPath in scanPaths)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                if (!Directory.Exists(scanPath))
                    continue;

                try
                {
                    var gmdFiles = Directory.EnumerateFiles(scanPath, "*.gmd",
                        new EnumerationOptions
                        {
                            RecurseSubdirectories = true,
                            IgnoreInaccessible = true,
                            AttributesToSkip = FileAttributes.System | FileAttributes.Temporary
                        });

                    foreach (var gmdFile in gmdFiles)
                    {
                        if (cancellationToken.IsCancellationRequested)
                            break;
                        foundGmdFiles.Add(gmdFile);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[AutoScan] 扫描路径失败 {scanPath}: {ex.Message}");
                }
            }

            result.TotalScanned = foundGmdFiles.Count;

            foreach (var gmdFile in foundGmdFiles)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                try
                {
                    var game = await _gmdFileService.DeserializeGameFromGmdAsync(gmdFile);
                    if (game == null) continue;

                    if (!string.IsNullOrEmpty(game.ExecutablePath) &&
                        existingExePaths.Contains(game.ExecutablePath))
                        continue;

                    if (existingGameNames.Contains(game.Name))
                        continue;

                    if (!string.IsNullOrWhiteSpace(game.GameId) &&
                        existingGameIds.Contains(game.GameId))
                        continue;

                    game.GmdFilePath = gmdFile;

                    var imageService = new ImageService();
                    imageService.EnsureGameImageDirectory(game.GameId);

                    var scanService = new DiskScanService();
                    var (iconPath, _) = await scanService.ExtractImagesFromGmdToLocalAsync(gmdFile, game.GameId);
                    if (!string.IsNullOrEmpty(iconPath))
                    {
                        game.IconPath = iconPath;
                    }

                    result.NewGameNames.Add(game.Name);
                    result.DiscoveredGames.Add(game);
                    existingGameNames.Add(game.Name);
                    if (!string.IsNullOrEmpty(game.ExecutablePath))
                        existingExePaths.Add(game.ExecutablePath);
                    if (!string.IsNullOrWhiteSpace(game.GameId))
                        existingGameIds.Add(game.GameId);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[AutoScan] 解析gmd失败 {gmdFile}: {ex.Message}");
                }
            }

            result.NewGamesFound = result.NewGameNames.Count;
            return result;
        }
    }
}