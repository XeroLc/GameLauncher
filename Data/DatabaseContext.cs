using Microsoft.Data.Sqlite;
using System;
using System.IO;
using System.Threading.Tasks;

namespace GameLauncher.Data
{
    public class DatabaseContext
    {
        private readonly string _databasePath;

        public DatabaseContext()
        {
            var appDataPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GameLauncher");
            _databasePath = Path.Combine(appDataPath, "games.db");
        }
        public string DatabasePath => _databasePath;

        public SqliteConnection GetConnection()
        {
            return new SqliteConnection($"Data Source={_databasePath}");
        }

        public async Task<SqliteConnection> GetOpenConnectionAsync()
        {
            var connection = new SqliteConnection($"Data Source={_databasePath}");
            await connection.OpenAsync();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "PRAGMA foreign_keys=ON;";
            await cmd.ExecuteNonQueryAsync();
            return connection;
        }

        public async Task InitializeAsync()
        {
            var dir = Path.GetDirectoryName(_databasePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                await Task.Run(() => Directory.CreateDirectory(dir));
            }

            var fileExists = await Task.Run(() => File.Exists(_databasePath));

            if (!fileExists)
            {
                using var connection = GetConnection();
                await connection.OpenAsync();

                using var command = connection.CreateCommand();
                command.CommandText = @"
                    CREATE TABLE Games (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        GameId TEXT,
                        Name TEXT NOT NULL,
                        ExecutablePath TEXT NOT NULL,
                        IconPath TEXT,
                        Description TEXT,
                        CreatedAt DATETIME DEFAULT CURRENT_TIMESTAMP,
                        LaunchCount INTEGER DEFAULT 0,
                        TotalPlayTime INTEGER DEFAULT 0,
                        LastRunTime DATETIME,
                        IsRunning INTEGER DEFAULT 0,
                        IsFavorite INTEGER DEFAULT 0,
                        ImagePaths TEXT,
                        Tags TEXT
                    );

                    CREATE TABLE IF NOT EXISTS GameCollections (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        Name TEXT NOT NULL UNIQUE,
                        CreatedAt DATETIME DEFAULT CURRENT_TIMESTAMP
                    );

                    CREATE TABLE IF NOT EXISTS GameCollectionItems (
                        GameId INTEGER NOT NULL,
                        CollectionId INTEGER NOT NULL,
                        PRIMARY KEY (GameId, CollectionId),
                        FOREIGN KEY (GameId) REFERENCES Games(Id) ON DELETE CASCADE,
                        FOREIGN KEY (CollectionId) REFERENCES GameCollections(Id) ON DELETE CASCADE
                    );

                    CREATE TABLE IF NOT EXISTS ConsistencyCheckLog (
                        GameId INTEGER PRIMARY KEY,
                        LastCheckTime TEXT NOT NULL,
                        WasConsistent INTEGER NOT NULL DEFAULT 1,
                        FOREIGN KEY (GameId) REFERENCES Games(Id)
                    );

                    CREATE TABLE IF NOT EXISTS SchemaVersion (
                        Key TEXT PRIMARY KEY,
                        Value TEXT NOT NULL
                    )";

                await command.ExecuteNonQueryAsync();
            }
            else
            {
                try
                {
                    await AddMissingColumnsAsync();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"数据库升级失败（非致命，继续使用现有结构）: {ex.Message}");
                }

                try
                {
                    using var ensureConn = GetConnection();
                    await ensureConn.OpenAsync();

                    using var ensureCmd1 = ensureConn.CreateCommand();
                    ensureCmd1.CommandText = @"
                        CREATE TABLE IF NOT EXISTS ConsistencyCheckLog (
                            GameId INTEGER PRIMARY KEY,
                            LastCheckTime TEXT NOT NULL,
                            WasConsistent INTEGER NOT NULL DEFAULT 1,
                            FOREIGN KEY (GameId) REFERENCES Games(Id)
                        )";
                    await ensureCmd1.ExecuteNonQueryAsync();

                    using var ensureCmd2 = ensureConn.CreateCommand();
                    ensureCmd2.CommandText = @"
                        CREATE TABLE IF NOT EXISTS SchemaVersion (
                            Key TEXT PRIMARY KEY,
                            Value TEXT NOT NULL
                        )";
                    await ensureCmd2.ExecuteNonQueryAsync();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"确保新表存在失败（非致命）: {ex.Message}");
                }
            }

            try
            {
                await MigrateFavoritesAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"收藏迁移初始化失败（非致命）: {ex.Message}");
            }

            try
            {
                using var pragmaConnection = GetConnection();
                await pragmaConnection.OpenAsync();

                using var walCmd = pragmaConnection.CreateCommand();
                walCmd.CommandText = "PRAGMA journal_mode=WAL;";
                await walCmd.ExecuteNonQueryAsync();

                using var timeoutCmd = pragmaConnection.CreateCommand();
                timeoutCmd.CommandText = "PRAGMA busy_timeout=5000;";
                await timeoutCmd.ExecuteNonQueryAsync();

                using var fkCmd = pragmaConnection.CreateCommand();
                fkCmd.CommandText = "PRAGMA foreign_keys=ON;";
                await fkCmd.ExecuteNonQueryAsync();

                using var idx1Cmd = pragmaConnection.CreateCommand();
                idx1Cmd.CommandText = "CREATE INDEX IF NOT EXISTS idx_games_gameid ON Games(GameId)";
                await idx1Cmd.ExecuteNonQueryAsync();

                using var idx2Cmd = pragmaConnection.CreateCommand();
                idx2Cmd.CommandText = "CREATE INDEX IF NOT EXISTS idx_collectionitems_collectionid ON GameCollectionItems(CollectionId)";
                await idx2Cmd.ExecuteNonQueryAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"PRAGMA/索引设置失败（非致命）: {ex.Message}");
            }
        }

        private async System.Threading.Tasks.Task AddMissingColumnsAsync()
        {
            using var connection = GetConnection();
            await connection.OpenAsync();

            // 先获取现有列
            var existingColumns = new System.Collections.Generic.List<string>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA table_info(Games)";
                using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    existingColumns.Add(reader.GetString(1));
                }
            }

            // 只添加不存在的列
            var columnsToAdd = new[]
            {
                ("GameId", "TEXT"),
                ("LaunchCount", "INTEGER DEFAULT 0"),
                ("TotalPlayTime", "INTEGER DEFAULT 0"),
                ("LastRunTime", "DATETIME"),
                ("IsRunning", "INTEGER DEFAULT 0"),
                ("IsFavorite", "INTEGER DEFAULT 0"),
                ("ImagePaths", "TEXT"),
                ("Tags", "TEXT")
            };

            foreach (var (columnName, columnDefinition) in columnsToAdd)
            {
                if (!existingColumns.Contains(columnName))
                {
                    using var command = connection.CreateCommand();
                    command.CommandText = $"ALTER TABLE Games ADD COLUMN {columnName} {columnDefinition}";
                    try
                    {
                        await command.ExecuteNonQueryAsync();
                        System.Diagnostics.Debug.WriteLine($"成功添加列: {columnName}");
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"添加列 {columnName} 失败: {ex.Message}");
                    }
                }
            }

            using (var checkCommand = connection.CreateCommand())
            {
                checkCommand.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='GameCollections'";
                var result = await checkCommand.ExecuteScalarAsync();
                if (result == null)
                {
                    using var createCommand = connection.CreateCommand();
                    createCommand.CommandText = @"
                        CREATE TABLE IF NOT EXISTS GameCollections (
                            Id INTEGER PRIMARY KEY AUTOINCREMENT,
                            Name TEXT NOT NULL UNIQUE,
                            CreatedAt DATETIME DEFAULT CURRENT_TIMESTAMP
                        )";
                    await createCommand.ExecuteNonQueryAsync();
                    System.Diagnostics.Debug.WriteLine("GameCollections 表创建成功");
                }
                else
                {
                    var hasIdColumn = false;
                    using var pragmaCommand = connection.CreateCommand();
                    pragmaCommand.CommandText = "PRAGMA table_info(GameCollections)";
                    using var pragmaReader = await pragmaCommand.ExecuteReaderAsync();
                    while (await pragmaReader.ReadAsync())
                    {
                        if (string.Equals(pragmaReader.GetString(1), "Id", StringComparison.OrdinalIgnoreCase))
                        {
                            hasIdColumn = true;
                            break;
                        }
                    }
                    if (!hasIdColumn)
                    {
                        System.Diagnostics.Debug.WriteLine("GameCollections 表缺少 Id 列，正在重建...");
                        using var dropCmd = connection.CreateCommand();
                        dropCmd.CommandText = "DROP TABLE IF EXISTS GameCollectionItems";
                        await dropCmd.ExecuteNonQueryAsync();
                        using var dropCmd2 = connection.CreateCommand();
                        dropCmd2.CommandText = "DROP TABLE IF EXISTS GameCollections";
                        await dropCmd2.ExecuteNonQueryAsync();
                        using var recreateCmd = connection.CreateCommand();
                        recreateCmd.CommandText = @"
                            CREATE TABLE GameCollections (
                                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                                Name TEXT NOT NULL UNIQUE,
                                CreatedAt DATETIME DEFAULT CURRENT_TIMESTAMP
                            )";
                        await recreateCmd.ExecuteNonQueryAsync();
                        using var recreateItemsCmd = connection.CreateCommand();
                        recreateItemsCmd.CommandText = @"
                            CREATE TABLE GameCollectionItems (
                                GameId INTEGER NOT NULL,
                                CollectionId INTEGER NOT NULL,
                                PRIMARY KEY (GameId, CollectionId),
                                FOREIGN KEY (GameId) REFERENCES Games(Id) ON DELETE CASCADE,
                                FOREIGN KEY (CollectionId) REFERENCES GameCollections(Id) ON DELETE CASCADE
                            )";
                        await recreateItemsCmd.ExecuteNonQueryAsync();
                        System.Diagnostics.Debug.WriteLine("GameCollections 表重建成功");
                    }
                }
            }

            using (var checkCommand = connection.CreateCommand())
            {
                checkCommand.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='GameCollectionItems'";
                var result = await checkCommand.ExecuteScalarAsync();
                if (result == null)
                {
                    using var createCommand = connection.CreateCommand();
                    createCommand.CommandText = @"
                        CREATE TABLE IF NOT EXISTS GameCollectionItems (
                            GameId INTEGER NOT NULL,
                            CollectionId INTEGER NOT NULL,
                            PRIMARY KEY (GameId, CollectionId),
                            FOREIGN KEY (GameId) REFERENCES Games(Id) ON DELETE CASCADE,
                            FOREIGN KEY (CollectionId) REFERENCES GameCollections(Id) ON DELETE CASCADE
                        )";
                    await createCommand.ExecuteNonQueryAsync();
                    System.Diagnostics.Debug.WriteLine("GameCollectionItems 表创建成功");
                }
            }
        }

        public async Task MigrateFavoritesAsync()
        {
            try
            {
                using var connection = GetConnection();
                await connection.OpenAsync();

                using var checkCommand = connection.CreateCommand();
                checkCommand.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='GameCollections'";
                var result = await checkCommand.ExecuteScalarAsync();
                if (result == null)
                {
                    using var createCommand = connection.CreateCommand();
                    createCommand.CommandText = @"
                        CREATE TABLE GameCollections (
                            Id INTEGER PRIMARY KEY AUTOINCREMENT,
                            Name TEXT NOT NULL UNIQUE,
                            CreatedAt DATETIME DEFAULT CURRENT_TIMESTAMP
                        )";
                    await createCommand.ExecuteNonQueryAsync();
                    System.Diagnostics.Debug.WriteLine("MigrateFavorites: GameCollections 表创建成功");
                }
                else
                {
                    var hasIdColumn = false;
                    using var pragmaCmd = connection.CreateCommand();
                    pragmaCmd.CommandText = "PRAGMA table_info(GameCollections)";
                    using var pragmaReader = await pragmaCmd.ExecuteReaderAsync();
                    while (await pragmaReader.ReadAsync())
                    {
                        if (string.Equals(pragmaReader.GetString(1), "Id", StringComparison.OrdinalIgnoreCase))
                        { hasIdColumn = true; break; }
                    }
                    if (!hasIdColumn)
                    {
                        System.Diagnostics.Debug.WriteLine("MigrateFavorites: GameCollections 缺少 Id 列，重建...");
                        using var d1 = connection.CreateCommand();
                        d1.CommandText = "DROP TABLE IF EXISTS GameCollectionItems";
                        await d1.ExecuteNonQueryAsync();
                        using var d2 = connection.CreateCommand();
                        d2.CommandText = "DROP TABLE IF EXISTS GameCollections";
                        await d2.ExecuteNonQueryAsync();
                        using var r1 = connection.CreateCommand();
                        r1.CommandText = @"CREATE TABLE GameCollections (Id INTEGER PRIMARY KEY AUTOINCREMENT, Name TEXT NOT NULL UNIQUE, CreatedAt DATETIME DEFAULT CURRENT_TIMESTAMP)";
                        await r1.ExecuteNonQueryAsync();
                        using var r2 = connection.CreateCommand();
                        r2.CommandText = @"CREATE TABLE GameCollectionItems (GameId INTEGER NOT NULL, CollectionId INTEGER NOT NULL, PRIMARY KEY (GameId, CollectionId), FOREIGN KEY (GameId) REFERENCES Games(Id) ON DELETE CASCADE, FOREIGN KEY (CollectionId) REFERENCES GameCollections(Id) ON DELETE CASCADE)";
                        await r2.ExecuteNonQueryAsync();
                    }
                }

                using var checkItemsCommand = connection.CreateCommand();
                checkItemsCommand.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='GameCollectionItems'";
                var itemsResult = await checkItemsCommand.ExecuteScalarAsync();
                if (itemsResult == null)
                {
                    using var createCommand = connection.CreateCommand();
                    createCommand.CommandText = @"
                        CREATE TABLE IF NOT EXISTS GameCollectionItems (
                            GameId INTEGER NOT NULL,
                            CollectionId INTEGER NOT NULL,
                            PRIMARY KEY (GameId, CollectionId),
                            FOREIGN KEY (GameId) REFERENCES Games(Id) ON DELETE CASCADE,
                            FOREIGN KEY (CollectionId) REFERENCES GameCollections(Id) ON DELETE CASCADE
                        )";
                    await createCommand.ExecuteNonQueryAsync();
                    System.Diagnostics.Debug.WriteLine("MigrateFavorites: GameCollectionItems 表创建成功");
                }

                var hasIsFavoriteColumn = false;
                using (var pragmaCommand = connection.CreateCommand())
                {
                    pragmaCommand.CommandText = "PRAGMA table_info(Games)";
                    using var pragmaReader = await pragmaCommand.ExecuteReaderAsync();
                    while (await pragmaReader.ReadAsync())
                    {
                        var colName = pragmaReader.GetString(1);
                        if (string.Equals(colName, "IsFavorite", StringComparison.OrdinalIgnoreCase))
                        {
                            hasIsFavoriteColumn = true;
                            break;
                        }
                    }
                }

                if (!hasIsFavoriteColumn)
                {
                    System.Diagnostics.Debug.WriteLine("MigrateFavorites: Games 表无 IsFavorite 列，跳过迁移");
                    return;
                }

                using var existsCommand = connection.CreateCommand();
                existsCommand.CommandText = "SELECT Id FROM GameCollections WHERE Name = '收藏的游戏'";
                var existingCollection = await existsCommand.ExecuteScalarAsync();
                if (existingCollection != null)
                {
                    System.Diagnostics.Debug.WriteLine("MigrateFavorites: 收藏的游戏 集合已存在，跳过迁移");
                    return;
                }

                using var countCommand = connection.CreateCommand();
                countCommand.CommandText = "SELECT COUNT(*) FROM Games WHERE IsFavorite = 1";
                var favoriteCount = (long)await countCommand.ExecuteScalarAsync();
                if (favoriteCount == 0)
                {
                    System.Diagnostics.Debug.WriteLine("MigrateFavorites: 没有收藏的游戏需要迁移");
                    return;
                }

                using var insertCommand = connection.CreateCommand();
                insertCommand.CommandText = "INSERT INTO GameCollections (Name, CreatedAt) VALUES ('收藏的游戏', CURRENT_TIMESTAMP)";
                await insertCommand.ExecuteNonQueryAsync();

                using var getIdCommand = connection.CreateCommand();
                getIdCommand.CommandText = "SELECT last_insert_rowid()";
                var collectionId = (long)await getIdCommand.ExecuteScalarAsync();

                using var selectFavoritesCommand = connection.CreateCommand();
                selectFavoritesCommand.CommandText = "SELECT Id FROM Games WHERE IsFavorite = 1";
                using var reader = await selectFavoritesCommand.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var gameId = reader.GetInt64(0);
                    using var linkCommand = connection.CreateCommand();
                    linkCommand.CommandText = "INSERT OR IGNORE INTO GameCollectionItems (GameId, CollectionId) VALUES ($gameId, $collectionId)";
                    linkCommand.Parameters.AddWithValue("$gameId", gameId);
                    linkCommand.Parameters.AddWithValue("$collectionId", collectionId);
                    await linkCommand.ExecuteNonQueryAsync();
                }

                System.Diagnostics.Debug.WriteLine($"MigrateFavorites: 成功迁移 {favoriteCount} 个收藏游戏到 收藏的游戏 集合");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"MigrateFavorites: 迁移失败（非致命）: {ex.Message}");
            }
        }
    }
}