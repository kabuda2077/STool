using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Serilog;
using STool.Core;

namespace STool.Modules.Clipboard;

/// <summary>
/// 剪贴板持久化存储。单连接由 _gate 串行保护，记录删除与图片路径收集使用同一事务。
/// </summary>
public class ClipboardStorage : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteConnection _connection;
    private readonly string _imagesDirectory;
    private readonly string _thumbnailsDirectory;
    private readonly object _gate = new();
    private bool _disposed;

    public ClipboardStorage()
        : this(AppPaths.ClipboardDbPath, AppPaths.ClipboardImagesDirectory, AppPaths.ClipboardThumbnailsDirectory)
    {
    }

    internal ClipboardStorage(string dbPath, string imagesDirectory, string thumbnailsDirectory)
    {
        _dbPath = dbPath;
        _imagesDirectory = imagesDirectory;
        _thumbnailsDirectory = thumbnailsDirectory;
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath) ?? ".");
        Directory.CreateDirectory(_imagesDirectory);
        Directory.CreateDirectory(_thumbnailsDirectory);
        _connection = new SqliteConnection($"Data Source={_dbPath}");
        _connection.Open();

        lock (_gate)
        {
            InitializeDatabase();
            RemoveMissingImageRecords();
            RemoveOrphanedImageFiles();
        }
    }

    private void InitializeDatabase()
    {
        const string createTableSql = @"
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
            command.ExecuteNonQuery();

        try
        {
            using var alter = new SqliteCommand("ALTER TABLE clipboard_items ADD COLUMN source_app TEXT", _connection);
            alter.ExecuteNonQuery();
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1 || ex.Message.Contains("duplicate column"))
        {
            Log.Debug("source_app column already exists, skipping migration");
        }

        Log.Information("Clipboard database initialized at {DatabasePath}", _dbPath);
    }

    public void Add(ClipboardItem item)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            const string sql = @"
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
            command.Parameters.AddWithValue("@created_at", item.CreatedAt.ToUniversalTime().ToString("o"));
            command.Parameters.AddWithValue("@is_favorite", item.IsFavorite ? 1 : 0);
            command.Parameters.AddWithValue("@tag", item.Tag ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@source_app", item.SourceApp ?? (object)DBNull.Value);
            command.ExecuteNonQuery();
        }
    }

    public List<ClipboardItem> GetRecent(int count = 100)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            using var command = new SqliteCommand(@"
                SELECT * FROM clipboard_items
                ORDER BY created_at DESC
                LIMIT @count
            ", _connection);
            command.Parameters.AddWithValue("@count", count);
            return ExecuteQuery(command);
        }
    }

    public List<ClipboardItem> Search(string keyword, int limit = 100)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            using var command = new SqliteCommand(@"
                SELECT * FROM clipboard_items
                WHERE text_content LIKE @keyword
                   OR file_paths LIKE @keyword
                   OR image_path LIKE @keyword
                   OR source_app LIKE @keyword
                   OR tag LIKE @keyword
                ORDER BY created_at DESC
                LIMIT @limit
            ", _connection);
            command.Parameters.AddWithValue("@keyword", $"%{keyword}%");
            command.Parameters.AddWithValue("@limit", limit);
            return ExecuteQuery(command);
        }
    }

    public List<ClipboardItem> GetFavorites()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            using var command = new SqliteCommand(@"
                SELECT * FROM clipboard_items
                WHERE is_favorite = 1
                ORDER BY created_at DESC
            ", _connection);
            return ExecuteQuery(command);
        }
    }

    public void ToggleFavorite(string id)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            using var command = new SqliteCommand(@"
                UPDATE clipboard_items
                SET is_favorite = 1 - is_favorite
                WHERE id = @id
            ", _connection);
            command.Parameters.AddWithValue("@id", id);
            command.ExecuteNonQuery();
        }
    }

    public void Delete(string id)
    {
        DeleteWhere("id = @id", command => command.Parameters.AddWithValue("@id", id));
    }

    public void CleanOldEntries(int retentionDays, int maxEntries)
    {
        var cutoffUtc = DateTime.UtcNow.AddDays(-retentionDays).ToString("o");
        var oldDeleted = DeleteWhere(
            "is_favorite = 0 AND created_at < @cutoff_date",
            command => command.Parameters.AddWithValue("@cutoff_date", cutoffUtc));
        if (oldDeleted > 0)
            Log.Information("Cleaned {DeletedCount} old clipboard entries", oldDeleted);

        var excessDeleted = DeleteWhere(@"
            is_favorite = 0
            AND id IN (
                SELECT id FROM clipboard_items
                WHERE is_favorite = 0
                ORDER BY created_at DESC
                LIMIT -1 OFFSET @max_entries
            )
        ", command => command.Parameters.AddWithValue("@max_entries", maxEntries));
        if (excessDeleted > 0)
            Log.Information("Cleaned {DeletedCount} excess clipboard entries", excessDeleted);
    }

    public void ClearAll() => DeleteWhere("is_favorite = 0", null);

    public void ClearByType(ClipboardItemType type) => DeleteWhere(
        "type = @type AND is_favorite = 0",
        command => command.Parameters.AddWithValue("@type", (int)type));

    public void ClearFavorites() => DeleteWhere("is_favorite = 1", null);

    private int DeleteWhere(string predicate, Action<SqliteCommand>? configure)
    {
        List<string> imagePaths;
        int deleted;
        lock (_gate)
        {
            ThrowIfDisposed();
            using var transaction = _connection.BeginTransaction();
            imagePaths = GetImagePaths(predicate, configure, transaction);

            using var command = new SqliteCommand($"DELETE FROM clipboard_items WHERE {predicate}", _connection, transaction);
            configure?.Invoke(command);
            deleted = command.ExecuteNonQuery();
            transaction.Commit();
        }

        DeleteImageFiles(imagePaths);
        return deleted;
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
                CreatedAt = ParseStoredDate(reader.GetString(5)),
                IsFavorite = reader.GetInt32(6) == 1,
                Tag = reader.IsDBNull(7) ? null : reader.GetString(7),
                SourceApp = reader.IsDBNull(srcOrdinal) ? null : reader.GetString(srcOrdinal)
            };

            if (!reader.IsDBNull(4))
                item.FilePaths = JsonSerializer.Deserialize<string[]>(reader.GetString(4));

            items.Add(item);
        }

        return items;
    }

    private static DateTime ParseStoredDate(string value)
    {
        if (!DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
            return DateTime.Now;

        return parsed.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(parsed, DateTimeKind.Local)
            : parsed.ToLocalTime();
    }

    private List<string> GetImagePaths(
        string predicate,
        Action<SqliteCommand>? configure,
        SqliteTransaction transaction)
    {
        var paths = new List<string>();
        using var command = new SqliteCommand(
            $"SELECT image_path FROM clipboard_items WHERE {predicate}",
            _connection,
            transaction);
        configure?.Invoke(command);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (!reader.IsDBNull(0))
                paths.Add(reader.GetString(0));
        }

        return paths;
    }

    private void RemoveMissingImageRecords()
    {
        var missingIds = new List<string>();
        using (var command = new SqliteCommand(
            "SELECT id, image_path FROM clipboard_items WHERE type = @type AND image_path IS NOT NULL",
            _connection))
        {
            command.Parameters.AddWithValue("@type", (int)ClipboardItemType.Image);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (!File.Exists(reader.GetString(1)))
                    missingIds.Add(reader.GetString(0));
            }
        }

        if (missingIds.Count == 0)
            return;

        using var transaction = _connection.BeginTransaction();
        foreach (var id in missingIds)
        {
            using var delete = new SqliteCommand("DELETE FROM clipboard_items WHERE id = @id", _connection, transaction);
            delete.Parameters.AddWithValue("@id", id);
            delete.ExecuteNonQuery();
        }
        transaction.Commit();
        Log.Information("Removed {Count} clipboard image records with missing files", missingIds.Count);
    }

    private void RemoveOrphanedImageFiles()
    {
        var referencedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var command = new SqliteCommand(
            "SELECT image_path FROM clipboard_items WHERE image_path IS NOT NULL",
            _connection))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
                referencedPaths.Add(Path.GetFullPath(reader.GetString(0)));
        }

        var deleted = 0;
        foreach (var path in Directory.EnumerateFiles(_imagesDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            if (referencedPaths.Contains(Path.GetFullPath(path)))
                continue;

            try
            {
                File.Delete(path);
                var thumbnailPath = GetThumbnailPath(path);
                if (File.Exists(thumbnailPath))
                    File.Delete(thumbnailPath);
                deleted++;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to delete orphaned clipboard image {ImagePath}", path);
            }
        }

        if (deleted > 0)
            Log.Information("Removed {Count} orphaned clipboard image files", deleted);
    }

    private void DeleteImageFiles(IEnumerable<string> paths)
    {
        foreach (var path in paths.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct())
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);

                var thumbPath = GetThumbnailPath(path);
                if (File.Exists(thumbPath))
                    File.Delete(thumbPath);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to delete clipboard image {ImagePath}", path);
            }
        }
    }

    private string GetThumbnailPath(string imagePath)
    {
        var fileName = Path.GetFileNameWithoutExtension(imagePath);
        return Path.Combine(_thumbnailsDirectory, fileName + ".thumb.png");
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _connection.Dispose();
        }
    }
}
