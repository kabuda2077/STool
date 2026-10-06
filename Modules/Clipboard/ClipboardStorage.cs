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

/// <summary>剪贴板列表查询条件。Type 为 null 表示全部类型。</summary>
public sealed record ClipboardQuery(
    ClipboardItemType? Type = null,
    bool FavoritesOnly = false,
    string? Keyword = null,
    int Offset = 0,
    int Limit = 100);

/// <summary>
/// 剪贴板持久化存储。单连接由 _gate 串行保护，记录删除与图片路径收集使用同一事务；
/// 文件系统扫描等耗时 IO 放在锁外执行，避免阻塞界面查询。
/// </summary>
public class ClipboardStorage : IDisposable
{
    private const int SqliteCorrupt = 11;
    private const int SqliteNotADatabase = 26;
    private const int MaintenanceBatchSize = 200;

    /// <summary>最近写入的图片可能还没来得及入库，孤儿清理跳过这段时间内的文件。</summary>
    private static readonly TimeSpan OrphanGracePeriod = TimeSpan.FromMinutes(10);

    private const string ListColumns =
        "id, type, text_preview, image_path, file_paths, created_at, is_favorite, tag, source_app, image_width, image_height, image_bytes, content_hash";
    private const string FullColumns = ListColumns + ", text_content";

    private readonly string _dbPath;
    private readonly string _imagesDirectory;
    private readonly string _thumbnailsDirectory;
    private readonly object _gate = new();
    private SqliteConnection _connection = null!;
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

        lock (_gate)
        {
            try
            {
                OpenAndInitialize();
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode is SqliteCorrupt or SqliteNotADatabase)
            {
                // 保留损坏库和关联图片，不在重建空库后把原历史图片误当作孤儿删除。
                Log.Error(ex, "Clipboard database is corrupt; preserving it for recovery");
                _connection?.Dispose();
                BackupCorruptDatabase();
                throw new InvalidDataException("剪贴板数据库损坏，原文件及图片已保留。请备份 Data 目录后恢复数据库。", ex);
            }
            catch
            {
                _connection?.Dispose();
                throw;
            }
        }
    }

    private void OpenAndInitialize()
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Pooling = false
        }.ToString();
        _connection = new SqliteConnection(connectionString);
        _connection.Open();

        // WAL + NORMAL 减少同步刷盘；断电仍可能丢失最近尚未检查点化的事务。
        Execute("PRAGMA journal_mode=WAL;");
        Execute("PRAGMA synchronous=NORMAL;");

        Execute(@"
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
            CREATE INDEX IF NOT EXISTS idx_favorite ON clipboard_items(is_favorite);");

        MigrateColumns();
        Execute("CREATE INDEX IF NOT EXISTS idx_content_hash ON clipboard_items(type, content_hash);");

        // 旧记录没有预览列，一次性用 SQL 补齐，列表查询从此不再读取完整文本。
        Execute($@"
            UPDATE clipboard_items
            SET text_preview = substr(text_content, 1, {ClipboardItem.TextPreviewLength})
            WHERE text_preview IS NULL AND text_content IS NOT NULL;");

        Log.Information("Clipboard database initialized at {DatabasePath}", _dbPath);
    }

    private void MigrateColumns()
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var command = new SqliteCommand("PRAGMA table_info(clipboard_items);", _connection))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
                existing.Add(reader.GetString(reader.GetOrdinal("name")));
        }

        var columns = new (string Name, string Type)[]
        {
            ("source_app", "TEXT"),
            ("text_preview", "TEXT"),
            ("image_width", "INTEGER"),
            ("image_height", "INTEGER"),
            ("image_bytes", "INTEGER"),
            ("content_hash", "TEXT")
        };
        foreach (var (name, type) in columns)
        {
            if (!existing.Contains(name))
                Execute($"ALTER TABLE clipboard_items ADD COLUMN {name} {type};");
        }
    }

    private void BackupCorruptDatabase()
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N");
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var path = _dbPath + suffix;
            if (!File.Exists(path))
                continue;
            try
            {
                File.Copy(path, $"{_dbPath}.corrupt-{stamp}{suffix}", overwrite: false);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to back up corrupt clipboard database file {Path}", path);
            }
        }
    }

    public void Add(ClipboardItem item)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            const string sql = @"
                INSERT INTO clipboard_items (
                    id, type, text_content, text_preview, image_path, file_paths, created_at,
                    is_favorite, tag, source_app, image_width, image_height, image_bytes, content_hash)
                VALUES (
                    @id, @type, @text_content, @text_preview, @image_path, @file_paths, @created_at,
                    @is_favorite, @tag, @source_app, @image_width, @image_height, @image_bytes, @content_hash)";

            using var command = new SqliteCommand(sql, _connection);
            command.Parameters.AddWithValue("@id", item.Id);
            command.Parameters.AddWithValue("@type", (int)item.Type);
            command.Parameters.AddWithValue("@text_content", DbValue(item.TextContent));
            command.Parameters.AddWithValue("@text_preview", DbValue(item.TextPreview ?? (item.TextContent == null ? null : ClipboardItem.CreatePreview(item.TextContent))));
            command.Parameters.AddWithValue("@image_path", DbValue(item.ImagePath));
            command.Parameters.AddWithValue("@file_paths", item.FilePaths != null
                ? JsonSerializer.Serialize(item.FilePaths)
                : DBNull.Value);
            command.Parameters.AddWithValue("@created_at", FormatDate(item.CreatedAt));
            command.Parameters.AddWithValue("@is_favorite", item.IsFavorite ? 1 : 0);
            command.Parameters.AddWithValue("@tag", DbValue(item.Tag));
            command.Parameters.AddWithValue("@source_app", DbValue(item.SourceApp));
            command.Parameters.AddWithValue("@image_width", item.ImageWidth > 0 ? item.ImageWidth : DBNull.Value);
            command.Parameters.AddWithValue("@image_height", item.ImageHeight > 0 ? item.ImageHeight : DBNull.Value);
            command.Parameters.AddWithValue("@image_bytes", item.ImageBytes > 0 ? item.ImageBytes : DBNull.Value);
            command.Parameters.AddWithValue("@content_hash", DbValue(item.ContentHash));
            command.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// 如果已有相同内容的记录，把它的时间更新为 <paramref name="copiedAt"/>（提到最前）并返回该记录；
    /// 否则返回 null，由调用方新增记录。
    /// </summary>
    public ClipboardItem? TryPromoteDuplicate(ClipboardItemType type, string contentHash, DateTime copiedAt, string? sourceApp)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            using var transaction = _connection.BeginTransaction();
            string? id;
            using (var find = new SqliteCommand(@"
                SELECT id FROM clipboard_items
                WHERE type = @type AND content_hash = @hash
                ORDER BY created_at DESC, id DESC
                LIMIT 1", _connection, transaction))
            {
                find.Parameters.AddWithValue("@type", (int)type);
                find.Parameters.AddWithValue("@hash", contentHash);
                id = find.ExecuteScalar() as string;
            }

            if (id == null)
                return null;

            using (var update = new SqliteCommand(@"
                UPDATE clipboard_items
                SET created_at = @created_at, source_app = COALESCE(@source_app, source_app)
                WHERE id = @id", _connection, transaction))
            {
                update.Parameters.AddWithValue("@created_at", FormatDate(copiedAt));
                update.Parameters.AddWithValue("@source_app", DbValue(sourceApp));
                update.Parameters.AddWithValue("@id", id);
                update.ExecuteNonQuery();
            }

            transaction.Commit();
            return QueryById(id, includeFullText: false);
        }
    }

    /// <summary>列表查询：只加载文本预览，不读取完整文本。</summary>
    public List<ClipboardItem> Query(ClipboardQuery query)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            using var command = _connection.CreateCommand();
            var conditions = new List<string>();
            if (query.Type is { } type)
            {
                conditions.Add("type = @type");
                command.Parameters.AddWithValue("@type", (int)type);
            }

            if (query.FavoritesOnly)
                conditions.Add("is_favorite = 1");

            if (!string.IsNullOrWhiteSpace(query.Keyword))
            {
                conditions.Add(@"(text_content LIKE @keyword ESCAPE '\'
                    OR file_paths LIKE @keyword ESCAPE '\'
                    OR image_path LIKE @keyword ESCAPE '\'
                    OR source_app LIKE @keyword ESCAPE '\'
                    OR tag LIKE @keyword ESCAPE '\')");
                command.Parameters.AddWithValue("@keyword", "%" + EscapeLike(query.Keyword.Trim()) + "%");
            }

            var where = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : string.Empty;
            command.CommandText = $@"
                SELECT {ListColumns} FROM clipboard_items
                {where}
                ORDER BY created_at DESC, id DESC
                LIMIT @limit OFFSET @offset";
            command.Parameters.AddWithValue("@limit", Math.Max(1, query.Limit));
            command.Parameters.AddWithValue("@offset", Math.Max(0, query.Offset));
            return ReadItems(command, includeFullText: false);
        }
    }

    /// <summary>读取包含完整文本的记录，用于复制还原。</summary>
    public ClipboardItem? GetById(string id)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            return QueryById(id, includeFullText: true);
        }
    }

    public List<ClipboardItem> GetRecent(int count = 100)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            using var command = new SqliteCommand($@"
                SELECT {FullColumns} FROM clipboard_items
                ORDER BY created_at DESC, id DESC
                LIMIT @count", _connection);
            command.Parameters.AddWithValue("@count", count);
            return ReadItems(command, includeFullText: true);
        }
    }

    /// <summary>切换收藏状态并返回切换后的状态；记录不存在时返回 null。</summary>
    public bool? ToggleFavorite(string id)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            using var command = new SqliteCommand(@"
                UPDATE clipboard_items
                SET is_favorite = 1 - is_favorite
                WHERE id = @id;
                SELECT is_favorite FROM clipboard_items WHERE id = @id;", _connection);
            command.Parameters.AddWithValue("@id", id);
            var value = command.ExecuteScalar();
            return value == null || value == DBNull.Value ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture) == 1;
        }
    }

    public void Delete(string id)
    {
        DeleteWhere("id = @id", command => command.Parameters.AddWithValue("@id", id));
    }

    /// <summary>
    /// 清理过期和超量的非收藏记录。<paramref name="retentionDays"/> 小于等于 0 时不按时间清理。
    /// </summary>
    public void CleanOldEntries(int retentionDays, int maxEntries)
    {
        if (retentionDays > 0)
        {
            var cutoffUtc = FormatDate(DateTime.UtcNow.AddDays(-retentionDays));
            var oldDeleted = DeleteWhere(
                "is_favorite = 0 AND created_at < @cutoff_date",
                command => command.Parameters.AddWithValue("@cutoff_date", cutoffUtc));
            if (oldDeleted > 0)
                Log.Information("Cleaned {DeletedCount} old clipboard entries", oldDeleted);
        }

        var excessDeleted = DeleteWhere(@"
            is_favorite = 0
            AND id IN (
                SELECT id FROM clipboard_items
                WHERE is_favorite = 0
                ORDER BY created_at DESC, id DESC
                LIMIT -1 OFFSET @max_entries
            )
        ", command => command.Parameters.AddWithValue("@max_entries", Math.Max(1, maxEntries)));
        if (excessDeleted > 0)
            Log.Information("Cleaned {DeletedCount} excess clipboard entries", excessDeleted);
    }

    public void ClearAll() => DeleteWhere("is_favorite = 0", null);

    public void ClearByType(ClipboardItemType type) => DeleteWhere(
        "type = @type AND is_favorite = 0",
        command => command.Parameters.AddWithValue("@type", (int)type));

    /// <summary>
    /// 后台维护：移除图片丢失的记录、清理无主图片与缩略图、补齐旧记录的图片尺寸和内容哈希。
    /// 文件系统访问都在锁外进行。
    /// </summary>
    public void RunMaintenance()
    {
        RemoveMissingImageRecords();
        RemoveOrphanedFiles();
        BackfillImageMetadata();
        BackfillContentHashes();
    }

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

    private ClipboardItem? QueryById(string id, bool includeFullText)
    {
        using var command = new SqliteCommand(
            $"SELECT {(includeFullText ? FullColumns : ListColumns)} FROM clipboard_items WHERE id = @id",
            _connection);
        command.Parameters.AddWithValue("@id", id);
        return ReadItems(command, includeFullText).FirstOrDefault();
    }

    private static List<ClipboardItem> ReadItems(SqliteCommand command, bool includeFullText)
    {
        var items = new List<ClipboardItem>();
        using var reader = command.ExecuteReader();
        var id = reader.GetOrdinal("id");
        var type = reader.GetOrdinal("type");
        var preview = reader.GetOrdinal("text_preview");
        var imagePath = reader.GetOrdinal("image_path");
        var filePaths = reader.GetOrdinal("file_paths");
        var createdAt = reader.GetOrdinal("created_at");
        var favorite = reader.GetOrdinal("is_favorite");
        var tag = reader.GetOrdinal("tag");
        var sourceApp = reader.GetOrdinal("source_app");
        var imageWidth = reader.GetOrdinal("image_width");
        var imageHeight = reader.GetOrdinal("image_height");
        var imageBytes = reader.GetOrdinal("image_bytes");
        var contentHash = reader.GetOrdinal("content_hash");
        var textContent = includeFullText ? reader.GetOrdinal("text_content") : -1;

        while (reader.Read())
        {
            var item = new ClipboardItem
            {
                Id = reader.GetString(id),
                Type = (ClipboardItemType)reader.GetInt32(type),
                TextPreview = ReadNullableString(reader, preview),
                ImagePath = ReadNullableString(reader, imagePath),
                CreatedAt = ParseStoredDate(reader.GetString(createdAt)),
                IsFavorite = !reader.IsDBNull(favorite) && reader.GetInt32(favorite) == 1,
                Tag = ReadNullableString(reader, tag),
                SourceApp = ReadNullableString(reader, sourceApp),
                ImageWidth = reader.IsDBNull(imageWidth) ? 0 : reader.GetInt32(imageWidth),
                ImageHeight = reader.IsDBNull(imageHeight) ? 0 : reader.GetInt32(imageHeight),
                ImageBytes = reader.IsDBNull(imageBytes) ? 0 : reader.GetInt64(imageBytes),
                ContentHash = ReadNullableString(reader, contentHash),
                TextContent = textContent >= 0 ? ReadNullableString(reader, textContent) : null
            };

            var paths = ReadNullableString(reader, filePaths);
            if (paths != null)
                item.FilePaths = JsonSerializer.Deserialize<string[]>(paths);

            items.Add(item);
        }

        return items;
    }

    private static string? ReadNullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static object DbValue(string? value) => value ?? (object)DBNull.Value;

    private static string FormatDate(DateTime value) => value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

    private static DateTime ParseStoredDate(string value)
    {
        if (!DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
            return DateTime.Now;

        return parsed.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(parsed, DateTimeKind.Local)
            : parsed.ToLocalTime();
    }

    internal static string EscapeLike(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);

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
        List<(string Id, string Path)> images;
        lock (_gate)
        {
            if (_disposed)
                return;
            images = ReadPairs("SELECT id, image_path FROM clipboard_items WHERE type = @type AND image_path IS NOT NULL",
                command => command.Parameters.AddWithValue("@type", (int)ClipboardItemType.Image));
        }

        var missingIds = images.Where(image => !File.Exists(image.Path)).Select(image => image.Id).ToList();
        if (missingIds.Count == 0)
            return;

        lock (_gate)
        {
            if (_disposed)
                return;
            using var transaction = _connection.BeginTransaction();
            foreach (var id in missingIds)
            {
                using var delete = new SqliteCommand("DELETE FROM clipboard_items WHERE id = @id", _connection, transaction);
                delete.Parameters.AddWithValue("@id", id);
                delete.ExecuteNonQuery();
            }
            transaction.Commit();
        }

        Log.Information("Removed {Count} clipboard image records with missing files", missingIds.Count);
    }

    private void RemoveOrphanedFiles()
    {
        HashSet<string> referencedPaths;
        lock (_gate)
        {
            if (_disposed)
                return;
            referencedPaths = new HashSet<string>(
                ReadPairs("SELECT id, image_path FROM clipboard_items WHERE image_path IS NOT NULL", null)
                    .Select(pair => Path.GetFullPath(pair.Path)),
                StringComparer.OrdinalIgnoreCase);
        }

        var cutoff = DateTime.UtcNow - OrphanGracePeriod;
        var deleted = 0;
        foreach (var path in SafeEnumerateFiles(_imagesDirectory))
        {
            if (referencedPaths.Contains(Path.GetFullPath(path)) || IsRecent(path, cutoff))
                continue;

            if (TryDeleteFile(path))
                deleted++;
            TryDeleteFile(GetThumbnailPath(path));
        }

        // 源图已不存在的缩略图也一并清理。
        var liveImageNames = new HashSet<string>(
            SafeEnumerateFiles(_imagesDirectory).Select(path => Path.GetFileNameWithoutExtension(path)),
            StringComparer.OrdinalIgnoreCase);
        foreach (var thumbnail in SafeEnumerateFiles(_thumbnailsDirectory))
        {
            var imageName = Path.GetFileName(thumbnail);
            if (imageName.EndsWith(".thumb.png", StringComparison.OrdinalIgnoreCase))
                imageName = imageName[..^".thumb.png".Length];
            if (!liveImageNames.Contains(imageName) && !IsRecent(thumbnail, cutoff))
                TryDeleteFile(thumbnail);
        }

        if (deleted > 0)
            Log.Information("Removed {Count} orphaned clipboard image files", deleted);
    }

    private void BackfillImageMetadata()
    {
        while (true)
        {
            List<(string Id, string Path)> pending;
            lock (_gate)
            {
                if (_disposed)
                    return;
                pending = ReadPairs($@"
                    SELECT id, image_path FROM clipboard_items
                    WHERE type = @type AND image_path IS NOT NULL AND image_width IS NULL
                    LIMIT {MaintenanceBatchSize}",
                    command => command.Parameters.AddWithValue("@type", (int)ClipboardItemType.Image));
            }

            if (pending.Count == 0)
                return;

            var updates = pending
                .Select(item =>
                {
                    var hasSize = TryReadPngSize(item.Path, out var width, out var height);
                    var bytes = File.Exists(item.Path) ? new FileInfo(item.Path).Length : 0;
                    // 读不到尺寸时记为 0，避免下次维护重复处理同一条记录。
                    return (item.Id, Width: hasSize ? width : 0, Height: hasSize ? height : 0, Bytes: bytes);
                })
                .ToList();

            lock (_gate)
            {
                if (_disposed)
                    return;
                using var transaction = _connection.BeginTransaction();
                foreach (var update in updates)
                {
                    using var command = new SqliteCommand(@"
                        UPDATE clipboard_items
                        SET image_width = @width, image_height = @height, image_bytes = @bytes
                        WHERE id = @id", _connection, transaction);
                    command.Parameters.AddWithValue("@width", update.Width);
                    command.Parameters.AddWithValue("@height", update.Height);
                    command.Parameters.AddWithValue("@bytes", update.Bytes);
                    command.Parameters.AddWithValue("@id", update.Id);
                    command.ExecuteNonQuery();
                }
                transaction.Commit();
            }

            if (pending.Count < MaintenanceBatchSize)
                return;
        }
    }

    private void BackfillContentHashes()
    {
        while (true)
        {
            var pending = new List<(string Id, ClipboardItemType Type, string? Text, string? FilePaths)>();
            lock (_gate)
            {
                if (_disposed)
                    return;
                using var command = new SqliteCommand($@"
                    SELECT id, type, text_content, file_paths FROM clipboard_items
                    WHERE content_hash IS NULL AND type IN (@text, @file)
                    LIMIT {MaintenanceBatchSize}", _connection);
                command.Parameters.AddWithValue("@text", (int)ClipboardItemType.Text);
                command.Parameters.AddWithValue("@file", (int)ClipboardItemType.File);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    pending.Add((
                        reader.GetString(0),
                        (ClipboardItemType)reader.GetInt32(1),
                        reader.IsDBNull(2) ? null : reader.GetString(2),
                        reader.IsDBNull(3) ? null : reader.GetString(3)));
                }
            }

            if (pending.Count == 0)
                return;

            var updates = pending
                .Select(item => (item.Id, Hash: item.Type == ClipboardItemType.Text
                    ? ClipboardContentHash.ForText(item.Text ?? string.Empty)
                    : ClipboardContentHash.ForText(item.FilePaths ?? string.Empty)))
                .ToList();

            lock (_gate)
            {
                if (_disposed)
                    return;
                using var transaction = _connection.BeginTransaction();
                foreach (var update in updates)
                {
                    using var command = new SqliteCommand(
                        "UPDATE clipboard_items SET content_hash = @hash WHERE id = @id",
                        _connection,
                        transaction);
                    command.Parameters.AddWithValue("@hash", update.Hash);
                    command.Parameters.AddWithValue("@id", update.Id);
                    command.ExecuteNonQuery();
                }
                transaction.Commit();
            }

            if (pending.Count < MaintenanceBatchSize)
                return;
        }
    }

    private List<(string Id, string Path)> ReadPairs(string sql, Action<SqliteCommand>? configure)
    {
        var pairs = new List<(string Id, string Path)>();
        using var command = new SqliteCommand(sql, _connection);
        configure?.Invoke(command);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (!reader.IsDBNull(1))
                pairs.Add((reader.GetString(0), reader.GetString(1)));
        }
        return pairs;
    }

    /// <summary>从 PNG 文件头读取原始尺寸(剪贴板图片均存为 PNG),避免整图解码。</summary>
    internal static bool TryReadPngSize(string path, out int width, out int height)
    {
        width = 0;
        height = 0;
        try
        {
            using var stream = File.OpenRead(path);
            var header = new byte[24];
            if (stream.Read(header, 0, header.Length) < header.Length)
                return false;

            // PNG 签名 89 50 4E 47;IHDR 中 width@16、height@20(大端)
            if (header[0] != 0x89 || header[1] != 0x50 || header[2] != 0x4E || header[3] != 0x47)
                return false;

            width = (header[16] << 24) | (header[17] << 16) | (header[18] << 8) | header[19];
            height = (header[20] << 24) | (header[21] << 16) | (header[22] << 8) | header[23];
            return width > 0 && height > 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void DeleteImageFiles(IEnumerable<string> paths)
    {
        foreach (var path in paths.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct())
        {
            TryDeleteFile(path);
            TryDeleteFile(GetThumbnailPath(path));
        }
    }

    private string GetThumbnailPath(string imagePath)
    {
        var fileName = Path.GetFileNameWithoutExtension(imagePath);
        return Path.Combine(_thumbnailsDirectory, fileName + ".thumb.png");
    }

    private static IEnumerable<string> SafeEnumerateFiles(string directory)
    {
        try
        {
            return Directory.Exists(directory)
                ? Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly).ToArray()
                : Array.Empty<string>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "Failed to enumerate clipboard directory {Directory}", directory);
            return Array.Empty<string>();
        }
    }

    private static bool IsRecent(string path, DateTime cutoffUtc)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path) > cutoffUtc;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static bool TryDeleteFile(string path)
    {
        try
        {
            if (!File.Exists(path))
                return false;
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "Failed to delete clipboard file {Path}", path);
            return false;
        }
    }

    private void Execute(string sql)
    {
        using var command = new SqliteCommand(sql, _connection);
        command.ExecuteNonQuery();
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
