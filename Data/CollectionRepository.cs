using GameLauncher.Models;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace GameLauncher.Data
{
    public class CollectionRepository
    {
        private readonly DatabaseContext _context;

        public CollectionRepository(DatabaseContext context)
        {
            _context = context;
        }

        public async Task<List<GameCollection>> GetAllCollectionsAsync()
        {
            try
            {
                var collections = new List<GameCollection>();

                using var connection = _context.GetConnection();
                await connection.OpenAsync();

                using var command = connection.CreateCommand();
                command.CommandText = @"
                    SELECT Id, Name, CreatedAt
                    FROM GameCollections
                    ORDER BY CreatedAt";

                using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    collections.Add(new GameCollection
                    {
                        Id = reader.GetInt32(0),
                        Name = reader.GetString(1),
                        CreatedAt = reader.GetDateTime(2)
                    });
                }

                return collections;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"GetAllCollectionsAsync 失败: {ex.Message}");
                return new List<GameCollection>();
            }
        }

        public async Task<GameCollection?> AddCollectionAsync(string name)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(name))
                    return null;

                using var connection = _context.GetConnection();
                await connection.OpenAsync();

                using var command = connection.CreateCommand();
                command.CommandText = @"
                    INSERT INTO GameCollections (Name, CreatedAt)
                    VALUES (@Name, @CreatedAt);
                    SELECT last_insert_rowid();";

                command.Parameters.AddWithValue("@Name", name);
                command.Parameters.AddWithValue("@CreatedAt", DateTime.UtcNow);

                var result = await command.ExecuteScalarAsync();
                int collectionId = Convert.ToInt32(result);

                System.Diagnostics.Debug.WriteLine($"创建游戏集合成功: {name} (Id={collectionId})");

                return new GameCollection
                {
                    Id = collectionId,
                    Name = name,
                    CreatedAt = DateTime.UtcNow
                };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"AddCollectionAsync 失败: {ex.Message}");
                return null;
            }
        }

        public async Task<bool> UpdateCollectionAsync(GameCollection collection)
        {
            try
            {
                using var connection = _context.GetConnection();
                await connection.OpenAsync();

                using var command = connection.CreateCommand();
                command.CommandText = @"
                    UPDATE GameCollections
                    SET Name = @Name
                    WHERE Id = @Id";

                command.Parameters.AddWithValue("@Name", collection.Name);
                command.Parameters.AddWithValue("@Id", collection.Id);

                int rowsAffected = await command.ExecuteNonQueryAsync();

                System.Diagnostics.Debug.WriteLine($"更新游戏集合: {collection.Name} (Id={collection.Id}), 成功={rowsAffected > 0}");

                return rowsAffected > 0;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"UpdateCollectionAsync 失败: {ex.Message}");
                return false;
            }
        }

        public async Task<bool> DeleteCollectionAsync(int id)
        {
            try
            {
                using var connection = _context.GetConnection();
                await connection.OpenAsync();

                using var transaction = connection.BeginTransaction();
                try
                {
                    using var delItemsCmd = connection.CreateCommand();
                    delItemsCmd.Transaction = transaction;
                    delItemsCmd.CommandText = "DELETE FROM GameCollectionItems WHERE CollectionId = @Id";
                    delItemsCmd.Parameters.AddWithValue("@Id", id);
                    await delItemsCmd.ExecuteNonQueryAsync();

                    using var delColCmd = connection.CreateCommand();
                    delColCmd.Transaction = transaction;
                    delColCmd.CommandText = "DELETE FROM GameCollections WHERE Id = @Id";
                    delColCmd.Parameters.AddWithValue("@Id", id);
                    int rowsAffected = await delColCmd.ExecuteNonQueryAsync();

                    transaction.Commit();
                    return rowsAffected > 0;
                }
                catch
                {
                    try { transaction.Rollback(); } catch { }
                    throw;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"DeleteCollectionAsync 失败: {ex.Message}");
                return false;
            }
        }

        public async Task<bool> AddGameToCollectionAsync(int gameId, int collectionId)
        {
            try
            {
                using var connection = _context.GetConnection();
                await connection.OpenAsync();

                using var command = connection.CreateCommand();
                command.CommandText = @"
                    INSERT OR IGNORE INTO GameCollectionItems (GameId, CollectionId)
                    VALUES (@GameId, @CollectionId)";

                command.Parameters.AddWithValue("@GameId", gameId);
                command.Parameters.AddWithValue("@CollectionId", collectionId);

                int rowsAffected = await command.ExecuteNonQueryAsync();

                System.Diagnostics.Debug.WriteLine($"添加游戏到集合: GameId={gameId}, CollectionId={collectionId}, 成功={rowsAffected > 0}");

                return rowsAffected > 0;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"AddGameToCollectionAsync 失败: {ex.Message}");
                return false;
            }
        }

        public async Task<bool> RemoveGameFromCollectionAsync(int gameId, int collectionId)
        {
            try
            {
                using var connection = _context.GetConnection();
                await connection.OpenAsync();

                using var command = connection.CreateCommand();
                command.CommandText = @"
                    DELETE FROM GameCollectionItems
                    WHERE GameId = @GameId AND CollectionId = @CollectionId";

                command.Parameters.AddWithValue("@GameId", gameId);
                command.Parameters.AddWithValue("@CollectionId", collectionId);

                int rowsAffected = await command.ExecuteNonQueryAsync();

                System.Diagnostics.Debug.WriteLine($"从集合移除游戏: GameId={gameId}, CollectionId={collectionId}, 成功={rowsAffected > 0}");

                return rowsAffected > 0;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"RemoveGameFromCollectionAsync 失败: {ex.Message}");
                return false;
            }
        }

        public async Task<List<GameCollection>> GetCollectionsForGameAsync(int gameId)
        {
            try
            {
                var collections = new List<GameCollection>();

                using var connection = _context.GetConnection();
                await connection.OpenAsync();

                using var command = connection.CreateCommand();
                command.CommandText = @"
                    SELECT c.Id, c.Name, c.CreatedAt
                    FROM GameCollections c
                    INNER JOIN GameCollectionItems ci ON c.Id = ci.CollectionId
                    WHERE ci.GameId = @GameId
                    ORDER BY c.Name";

                command.Parameters.AddWithValue("@GameId", gameId);

                using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    collections.Add(new GameCollection
                    {
                        Id = reader.GetInt32(0),
                        Name = reader.GetString(1),
                        CreatedAt = reader.GetDateTime(2)
                    });
                }

                return collections;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"GetCollectionsForGameAsync 失败 (gameId={gameId}): {ex.Message}");
                return new List<GameCollection>();
            }
        }

        public async Task<int> GetCollectionGameCountAsync(int collectionId)
        {
            try
            {
                using var connection = _context.GetConnection();
                await connection.OpenAsync();

                using var command = connection.CreateCommand();
                command.CommandText = @"
                    SELECT COUNT(*)
                    FROM GameCollectionItems
                    WHERE CollectionId = @CollectionId";

                command.Parameters.AddWithValue("@CollectionId", collectionId);

                var result = await command.ExecuteScalarAsync();
                return Convert.ToInt32(result);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"GetCollectionGameCountAsync 失败: {ex.Message}");
                return 0;
            }
        }

        public async Task<Dictionary<int, int>> GetCollectionGameCountsAsync()
        {
            try
            {
                var result = new Dictionary<int, int>();
                using var connection = _context.GetConnection();
                await connection.OpenAsync();
                using var command = connection.CreateCommand();
                command.CommandText = @"
                    SELECT CollectionId, COUNT(*) as GameCount
                    FROM GameCollectionItems
                    GROUP BY CollectionId";
                using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    result[reader.GetInt32(0)] = reader.GetInt32(1);
                }
                return result;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"GetCollectionGameCountsAsync 失败: {ex.Message}");
                return new Dictionary<int, int>();
            }
        }

        public async Task<Dictionary<int, List<GameCollection>>> GetAllGameCollectionMappingsAsync()
        {
            try
            {
                var result = new Dictionary<int, List<GameCollection>>();

                using var connection = _context.GetConnection();
                await connection.OpenAsync();

                using var command = connection.CreateCommand();
                command.CommandText = @"
                    SELECT ci.GameId, c.Id, c.Name, c.CreatedAt
                    FROM GameCollectionItems ci
                    INNER JOIN GameCollections c ON ci.CollectionId = c.Id
                    ORDER BY c.Name";

                using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var gameId = reader.GetInt32(0);
                    var collection = new GameCollection
                    {
                        Id = reader.GetInt32(1),
                        Name = reader.GetString(2),
                        CreatedAt = reader.GetDateTime(3)
                    };

                    if (!result.ContainsKey(gameId))
                    {
                        result[gameId] = new List<GameCollection>();
                    }
                    result[gameId].Add(collection);
                }

                return result;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"GetAllGameCollectionMappingsAsync 失败: {ex.Message}");
                return new Dictionary<int, List<GameCollection>>();
            }
        }
    }
}