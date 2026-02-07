using GameLauncher.Data;
using GameLauncher.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;

namespace GameLauncher.Services
{
    public class GameService
    {
        private readonly GameRepository _repository;

        public GameService(GameRepository repository)
        {
            _repository = repository;
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

            return await _repository.UpdateGameAsync(game);
        }

        public async Task<bool> DeleteGameAsync(int id)
        {
            return await _repository.DeleteGameAsync(id);
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

                var startInfo = new ProcessStartInfo
                {
                    FileName = game.ExecutablePath,
                    UseShellExecute = true,
                    WorkingDirectory = System.IO.Path.GetDirectoryName(game.ExecutablePath)
                };

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

            return await _repository.UpdateGameAsync(game);
        }

        public async Task<bool> UpdateGameRunningStatusAsync(int gameId, bool isRunning)
        {
            var game = await _repository.GetGameByIdAsync(gameId);
            if (game == null)
            {
                return false;
            }

            game.IsRunning = isRunning;

            return await _repository.UpdateGameAsync(game);
        }
    }
}