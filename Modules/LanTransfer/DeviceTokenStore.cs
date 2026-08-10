using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.IO;
using Serilog;
using STool.Core;

namespace STool.Modules.LanTransfer;

internal sealed record RememberedDevice(string TokenHash, DateTimeOffset CreatedUtc, DateTimeOffset ExpiresUtc, DateTimeOffset LastSeenUtc);

internal sealed class DeviceTokenStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _gate = new();
    private readonly string _path;
    private List<RememberedDevice> _devices;

    public DeviceTokenStore(string? path = null)
    {
        _path = path ?? AppPaths.LanTransferDevicesPath;
        _devices = Load();
    }

    public string Issue()
    {
        var token = CreateToken();
        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            RemoveExpired(now);
            _devices.Add(new RememberedDevice(Hash(token), now, now.AddDays(90), now));
            Save();
        }
        return token;
    }

    public bool Validate(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return false;

        var hash = Hash(token);
        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            RemoveExpired(now);
            var index = _devices.FindIndex(item => FixedEquals(item.TokenHash, hash));
            if (index < 0)
                return false;

            _devices[index] = _devices[index] with { LastSeenUtc = now };
            Save();
            return true;
        }
    }

    public void RevokeAll()
    {
        lock (_gate)
        {
            _devices.Clear();
            Save();
        }
    }

    private List<RememberedDevice> Load()
    {
        try
        {
            if (!File.Exists(_path))
                return [];
            return JsonSerializer.Deserialize<List<RememberedDevice>>(
                File.ReadAllText(_path), JsonOptions) ?? [];
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load remembered LAN transfer devices");
            return [];
        }
    }

    private void Save()
    {
        var path = _path;
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        var tempPath = path + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(_devices, JsonOptions));
        File.Move(tempPath, path, true);
    }

    private void RemoveExpired(DateTimeOffset now)
    {
        _devices.RemoveAll(item => item.ExpiresUtc <= now);
    }

    private static string CreateToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static bool FixedEquals(string left, string right)
    {
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(left), Encoding.ASCII.GetBytes(right));
    }
}
