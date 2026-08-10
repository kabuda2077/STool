using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace STool.Modules.LanTransfer;

internal sealed class LanTransferAuthService
{
    public const string SessionCookieName = "stool_session";
    public const string DeviceCookieName = "stool_device";

    private readonly DeviceTokenStore _deviceTokenStore;
    private readonly HashSet<string> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FailureState> _failures = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public LanTransferAuthService(DeviceTokenStore deviceTokenStore)
    {
        _deviceTokenStore = deviceTokenStore;
        RotateAccessCodes();
    }

    public string QrToken { get; private set; } = string.Empty;
    public string ManualCode { get; private set; } = string.Empty;

    public void RotateAccessCodes()
    {
        QrToken = CreateToken();
        ManualCode = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
    }

    public bool TryExchange(AuthExchangeRequest request, IPAddress remoteAddress, out string sessionToken, out string? deviceToken)
    {
        sessionToken = string.Empty;
        deviceToken = null;
        var key = remoteAddress.ToString();

        lock (_gate)
        {
            if (_failures.TryGetValue(key, out var failure) && failure.LockedUntilUtc > DateTimeOffset.UtcNow)
                return false;

            var tokenMatch = FixedEquals(request.Token, QrToken);
            var codeMatch = FixedEquals(request.Code, ManualCode);
            if (!tokenMatch && !codeMatch)
            {
                var count = (_failures.TryGetValue(key, out failure) ? failure.Count : 0) + 1;
                _failures[key] = count >= 6
                    ? new FailureState(0, DateTimeOffset.UtcNow.AddMinutes(1))
                    : new FailureState(count, DateTimeOffset.MinValue);
                return false;
            }

            _failures.Remove(key);
            sessionToken = CreateToken();
            _sessions.Add(sessionToken);
            if (request.Remember)
                deviceToken = _deviceTokenStore.Issue();
            return true;
        }
    }

    public bool TryAuthenticate(string? sessionToken, string? deviceToken, out string? issuedSessionToken)
    {
        issuedSessionToken = null;
        lock (_gate)
        {
            if (!string.IsNullOrWhiteSpace(sessionToken) && _sessions.Contains(sessionToken))
                return true;
        }

        if (!_deviceTokenStore.Validate(deviceToken))
            return false;

        issuedSessionToken = CreateToken();
        lock (_gate)
            _sessions.Add(issuedSessionToken);
        return true;
    }

    public void RemoveSession(string? sessionToken)
    {
        if (string.IsNullOrWhiteSpace(sessionToken))
            return;
        lock (_gate)
            _sessions.Remove(sessionToken);
    }

    public void Clear()
    {
        lock (_gate)
        {
            _sessions.Clear();
            _failures.Clear();
        }
        RotateAccessCodes();
    }

    private static string CreateToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool FixedEquals(string? left, string right)
    {
        if (string.IsNullOrEmpty(left))
            return false;
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private sealed record FailureState(int Count, DateTimeOffset LockedUntilUtc);
}
