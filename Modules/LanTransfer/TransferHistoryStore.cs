using System.Text.Json;
using System.IO;
using Serilog;
using STool.Core;

namespace STool.Modules.LanTransfer;

internal sealed class TransferHistoryStore
{
    private const int MaximumEntries = 100;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private readonly object _gate = new();
    private readonly string _path;
    private List<TransferHistoryEntry> _entries;

    public TransferHistoryStore(string? path = null)
    {
        _path = path ?? AppPaths.LanTransferHistoryPath;
        _entries = Load();
    }

    public IReadOnlyList<TransferHistoryEntry> Snapshot()
    {
        lock (_gate)
            return _entries.OrderByDescending(item => item.CompletedUtc).ToArray();
    }

    public void Add(TransferHistoryEntry entry)
    {
        lock (_gate)
        {
            _entries.RemoveAll(item => string.Equals(item.Id, entry.Id, StringComparison.OrdinalIgnoreCase));
            _entries.Insert(0, entry);
            if (_entries.Count > MaximumEntries)
                _entries.RemoveRange(MaximumEntries, _entries.Count - MaximumEntries);
            SaveLocked();
        }
    }

    public void Remove(string id)
    {
        lock (_gate)
        {
            if (_entries.RemoveAll(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase)) == 0)
                return;
            SaveLocked();
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            if (_entries.Count == 0)
                return;
            _entries.Clear();
            SaveLocked();
        }
    }

    private List<TransferHistoryEntry> Load()
    {
        if (!File.Exists(_path))
            return [];
        try
        {
            var entries = JsonSerializer.Deserialize<List<TransferHistoryEntry>>(File.ReadAllText(_path), JsonOptions) ?? [];
            return entries.OrderByDescending(item => item.CompletedUtc).Take(MaximumEntries).ToList();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load LAN transfer history");
            return [];
        }
    }

    private void SaveLocked()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tempPath = _path + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(_entries, JsonOptions));
        File.Move(tempPath, _path, true);
    }
}
