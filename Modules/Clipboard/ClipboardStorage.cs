using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Serilog;

namespace STool.Modules.Clipboard;

/// <summary>
/// 剪贴板持久化存储
/// </summary>
public class ClipboardStorage : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteConnection _connection;

    public ClipboardStorage()
    {
        var appDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "STool"
        );

        Directory.CreateDirectory(appDataPath);
        _dbPath = Path.Combine(appDataPath, "clipboard.db");

        _connection = new SqliteConnection($"Data Source={_dbPath}");
        _connection.Open();

        InitializeDatabase();
    }

    private void InitializeDatabase()
    {
        var createTableSql = @"
            CREATE TABLE IF NOT EXISTS clipboard_items (
                id TEXT PRIMARY KEY,
                type INTEGER NOT NULL,
                text_content TEXT,
                image_path TEXT,
                file_paths TEXT,
                created_at TEXT NOT NULL,
                is_favorite INTEGER DEFAULT 0,
                tag TEXT,
                source_app TEXT
            );

            CREATE INDEX IF NOT EXISTS idx_created_at ON clipboard_items(created_at DESC);
            CREATE INDEX IF NOT EXISTS idx_type ON clipboard_items(type);
            CREATE INDEX IF NOT EXISTS idx_favorite ON clipboard_items(is_favorite);
        ";

        using (var command = new SqliteCommand(createTableSql, _connection))
        {
            command.ExecuteNonQuery();
        }

        // 旧库迁移:补充 source_app 列(已存在则忽略)
        try
        {
            using var alter = new SqliteCommand("ALTER TABLE clipboard_items ADD COLUMN source_app TEXT", _connection);
            alter.ExecuteNonQuery();
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1 || ex.Message.Contains("duplicate column"))
        {
            // 列已存在,忽略
            Log.Debug("source_app column already exists, skipping migration");
        }

        Log.Information($"Clipboard database initialized at {_dbPath}");
    }

    public void Add(ClipboardItem item)
    {
        var sql = @"
            INSERT INTO clipboard_items (id, type, text_content, image_path, file_paths, created_at, is_favorite, tag, source_app)
            VALUES (@id, @type, @text_content, @image_path, @file_paths, @created_at, @is_favorite, @tag, @source_app)
        ";

        using var command = new SqliteCommand(sql, _connection);
        command.Parameters.AddWithValue("@id", item.Id);
        command.Parameters.AddWithValue("@type", (int)item.Type);
        command.Parameters.AddWithValue("@text_content", item.TextContent ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@image_path", item.ImagePath ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@file_paths", item.FilePaths != null
            ? JsonSerializer.Serialize(item.FilePaths)
            : (object)DBNull.Value);
        command.Parameters.AddWithValue("@created_at", item.CreatedAt.ToString("o"));
        command.Parameters.AddWithValue("@is_favorite", item.IsFavorite ? 1 : 0);
        command.Parameters.AddWithValue("@tag", item.Tag ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@source_app", item.SourceApp ?? (object)DBNull.Value);

        command.ExecuteNonQuery();
    }

    public List<ClipboardItem> GetRecent(int count = 100)
    {
        var sql = @"
            SELECT * FROM clipboard_items
            ORDER BY created_at DESC
            LIMIT @count
        ";

        using var command = new SqliteCommand(sql, _connection);
        command.Parameters.AddWithValue("@count", count);

        return ExecuteQuery(command);
    }

    public List<ClipboardItem> Search(string keyword, int limit = 100)
    {
        var sql = @"
            SELECT * FROM clipboard_items
            WHERE text_content LIKE @keyword
            ORDER BY created_at DESC
            LIMIT @limit
        ";

        using var command = new SqliteCommand(sql, _connection);
        command.Parameters.AddWithValue("@keyword", $"%{keyword}%");
        command.Parameters.AddWithValue("@limit", limit);

        return ExecuteQuery(command);
    }

    public List<ClipboardItem> GetFavorites()
    {
        var sql = @"
            SELECT * FROM clipboard_items
            WHERE is_favorite = 1
            ORDER BY created_at DESC
        ";

        using var command = new SqliteCommand(sql, _connection);
        return ExecuteQuery(command);
    }

    public void ToggleFavorite(string id)
    {
        var sql = @"
            UPDATE clipboard_items
            SET is_favorite = 1 - is_favorite
            WHERE id = @id
        ";

        using var command = new SqliteCommand(sql, _connection);
        command.Parameters.AddWithValue("@id", id);
        command.ExecuteNonQuery();
    }

    public void Delete(string id)
    {
        var imagePaths = GetImagePaths("WHERE id = @id", command =>
        {
            command.Parameters.AddWithValue("@id", id);
        });

        var sql = "DELETE FROM clipboard_items WHERE id = @id";

        using var command = new SqliteCommand(sql, _connection);
        command.Parameters.AddWithValue("@id", id);
        command.ExecuteNonQuery();

        DeleteImageFiles(imagePaths);
    }

    public void CleanOldEntries(int retentionDays, int maxEntries)
    {
        // 删除超过保留天数的非收藏条目
        var cutoffDate = DateTime.Now.AddDays(-retentionDays);
        var oldImagePaths = GetImagePaths("WHERE is_favorite = 0 AND created_at < @cutoff_date", command =>
        {
            command.Parameters.AddWithValue("@cutoff_date", cutoffDate.ToString("o"));
        });

        var sql1 = @"
            DELETE FROM clipboard_items
            WHERE is_favorite = 0 AND created_at < @cutoff_date
        ";

        using (var command = new SqliteCommand(sql1, _connection))
        {
            command.Parameters.AddWithValue("@cutoff_date", cutoffDate.ToString("o"));
            var deleted = command.ExecuteNonQuery();
            if (deleted > 0)
            {
                Log.Information($"Cleaned {deleted} old clipboard entries");
                DeleteImageFiles(oldImagePaths);
            }
        }

        // 如果超过最大条目数，删除最旧的非收藏条目
        var excessImagePaths = GetImagePaths(@"
            WHERE is_favorite = 0
            AND id IN (
                SELECT id FROM clipboard_items
                WHERE is_favorite = 0
                ORDER BY created_at DESC
                LIMIT -1 OFFSET @max_entries
            )
        ", command =>
        {
            command.Parameters.AddWithValue("@max_entries", maxEntries);
        });

        var sql2 = @"
            DELETE FROM clipboard_items
            WHERE is_favorite = 0
            AND id IN (
                SELECT id FROM clipboard_items
                WHERE is_favorite = 0
                ORDER BY created_at DESC
                LIMIT -1 OFFSET @max_entries
            )
        ";

        using (var command = new SqliteCommand(sql2, _connection))
        {
            command.Parameters.AddWithValue("@max_entries", maxEntries);
            var deleted = command.ExecuteNonQuery();
            if (deleted > 0)
            {
                Log.Information($"Cleaned {deleted} excess clipboard entries");
                DeleteImageFiles(excessImagePaths);
            }
        }
    }

    public void ClearAll()
    {
        var imagePaths = GetImagePaths("", null);

        using (var command = new SqliteCommand("DELETE FROM clipboard_items", _connection))
        {
            command.ExecuteNonQuery();
        }

        DeleteImageFiles(imagePaths);
    }

    private List<ClipboardItem> ExecuteQuery(SqliteCommand command)
    {
        var items = new List<ClipboardItem>();

        using var reader = command.ExecuteReader();
        var srcOrdinal = reader.GetOrdinal("source_app");
        while (reader.Read())
        {
            var item = new ClipboardItem
            {
                Id = reader.GetString(0),
                Type = (ClipboardItemType)reader.GetInt32(1),
                TextContent = reader.IsDBNull(2) ? null : reader.GetString(2),
                ImagePath = reader.IsDBNull(3) ? null : reader.GetString(3),
                CreatedAt = DateTime.Parse(reader.GetString(5)),
                IsFavorite = reader.GetInt32(6) == 1,
                Tag = reader.IsDBNull(7) ? null : reader.GetString(7),
                SourceApp = reader.IsDBNull(srcOrdinal) ? null : reader.GetString(srcOrdinal)
            };

            if (!reader.IsDBNull(4))
            {
                item.FilePaths = JsonSerializer.Deserialize<string[]>(reader.GetString(4));
            }

            items.Add(item);
        }

        return items;
    }

    public void Dispose()
    {
        _connection?.Dispose();
    }

    private List<string> GetImagePaths(string whereClause, Action<SqliteCommand>? configure)
    {
        var paths = new List<string>();
        var sql = $"SELECT image_path FROM clipboard_items {whereClause}";

        using var command = new SqliteCommand(sql, _connection);
        configure?.Invoke(command);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (!reader.IsDBNull(0))
            {
                paths.Add(reader.GetString(0));
            }
        }

        return paths;
    }

    private static void DeleteImageFiles(IEnumerable<string> paths)
    {
        foreach (var path in paths.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct())
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, $"Failed to delete clipboard image: {path}");
            }
        }
    }
}
