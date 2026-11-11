using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace STool.Modules.LanTransfer;

/// <summary>
/// 局域网传输的配对与会话认证。会话绑定客户端 IP；二维码令牌与验证码有有效期，
/// 到期、配对成功或全局失败次数达到阈值时自动更换。失败次数按来源 IP 限流，
/// 全局阈值只触发换码而不锁定，避免任何人都能靠输错把正常用户挡在门外。
/// </summary>
internal sealed class LanTransferAuthService : IDisposable
{
    public const string SessionCookieName = "stool_session";
    public const string DeviceCookieName = "stool_device";

    private const int MaxFailuresPerAddress = 6;
    private static readonly TimeSpan AddressLockout = TimeSpan.FromMinutes(1);
    private const int MaxGlobalFailures = 20;
    private static readonly TimeSpan GlobalFailureWindow = TimeSpan.FromMinutes(5);
    /// <summary>二维码令牌与验证码的有效期，到期自动轮换。</summary>
    internal static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ExpiryCheckInterval = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(12);

    private readonly DeviceTokenStore _deviceTokenStore;
    private readonly Func<DateTimeOffset> _now;
    private readonly Dictionary<string, SessionEntry> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FailureState> _failures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<DateTimeOffset> _recentFailures = new();
    private readonly object _gate = new();
    private readonly Timer? _expiryTimer;

    /// <param name="autoRotate">为 true 时后台定时检查有效期并触发 <see cref="AccessCodesRotated"/>（界面据此刷新）。</param>
    public LanTransferAuthService(DeviceTokenStore deviceTokenStore, Func<DateTimeOffset>? now = null, bool autoRotate = false)
    {
        _deviceTokenStore = deviceTokenStore;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        lock (_gate)
            RotateAccessCodesLocked();
        if (autoRotate)
            _expiryTimer = new Timer(_ => RotateIfExpired(), null, ExpiryCheckInterval, ExpiryCheckInterval);
    }

    public void Dispose() => _expiryTimer?.Dispose();

    /// <summary>当前二维码令牌与验证码的到期时间（UTC）。</summary>
    public DateTimeOffset CodesExpireUtc { get; private set; }

    /// <summary>已过期则换码并通知界面；返回是否发生了轮换。</summary>
    public bool RotateIfExpired()
    {
        lock (_gate)
        {
            if (_now() < CodesExpireUtc)
                return false;
            RotateAccessCodesLocked();
        }
        AccessCodesRotated?.Invoke();
        return true;
    }

    /// <summary>移除所有已记住的设备，同时让当前所有在线会话失效。</summary>
    public void RevokeRememberedDevices()
    {
        lock (_gate)
        {
            _deviceTokenStore.RevokeAll();
            _sessions.Clear();
        }
    }

    /// <summary>二维码令牌或验证码已更换，界面应刷新显示。</summary>
    public event Action? AccessCodesRotated;

    public string QrToken { get; private set; } = string.Empty;
    public string ManualCode { get; private set; } = string.Empty;

    public void RotateAccessCodes()
    {
        lock (_gate)
            RotateAccessCodesLocked();
        AccessCodesRotated?.Invoke();
    }

    private void RotateAccessCodesLocked()
    {
        QrToken = CreateToken();
        string code;
        do
        {
            code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
        } while (code == ManualCode);
        ManualCode = code;
        CodesExpireUtc = _now() + CodeLifetime;
    }

    private void RemoveExpiredSessionsLocked(DateTimeOffset now)
    {
        foreach (var token in _sessions.Where(pair => pair.Value.ExpiresUtc <= now).Select(pair => pair.Key).ToArray())
            _sessions.Remove(token);
    }

    public bool TryExchange(AuthExchangeRequest request, IPAddress remoteAddress, out string sessionToken, out string? deviceToken)
    {
        sessionToken = string.Empty;
        deviceToken = null;
        var address = Normalize(remoteAddress);
        var key = address.ToString();
        var rotate = false;

        lock (_gate)
        {
            var now = _now();
            // 过期的验证码不再接受：先换码，本次提交的旧码自然失败。
            if (now >= CodesExpireUtc)
            {
                RotateAccessCodesLocked();
                rotate = true;
            }
            if (_failures.TryGetValue(key, out var failure) && failure.LockedUntilUtc > now)
                return false;

            var tokenMatch = FixedEquals(request.Token, QrToken);
            var codeMatch = FixedEquals(request.Code, ManualCode);
            if (!tokenMatch && !codeMatch)
            {
                var count = (_failures.TryGetValue(key, out failure) ? failure.Count : 0) + 1;
                _failures[key] = count >= MaxFailuresPerAddress
                    ? new FailureState(0, now + AddressLockout)
                    : new FailureState(count, DateTimeOffset.MinValue);

                // 全局计数防止换多个 IP 并行猜测：达到阈值只换码，不锁定正常用户。
                _recentFailures.Enqueue(now);
                while (_recentFailures.Count > 0 && now - _recentFailures.Peek() > GlobalFailureWindow)
                    _recentFailures.Dequeue();
                if (_recentFailures.Count >= MaxGlobalFailures)
                {
                    _recentFailures.Clear();
                    rotate = true;
                }
            }
            else
            {
                _failures.Remove(key);
                if (request.Remember)
                    deviceToken = _deviceTokenStore.Issue();
                RemoveExpiredSessionsLocked(now);
                sessionToken = CreateToken();
                _sessions[sessionToken] = new SessionEntry(address, now + SessionLifetime);
                rotate = true;
            }

            // 消费配对码和生成下一组码必须在同一锁内，否则并发请求能重复消费旧码。
            if (rotate)
                RotateAccessCodesLocked();
        }

        if (rotate)
            AccessCodesRotated?.Invoke();
        return sessionToken.Length > 0;
    }

    /// <summary>
    /// 校验会话；会话只在创建时的 IP 上有效。会话无效但带有已记住设备的令牌时，签发绑定当前 IP 的新会话。
    /// </summary>
    public bool TryAuthenticate(string? sessionToken, string? deviceToken, IPAddress remoteAddress, out string? issuedSessionToken)
    {
        issuedSessionToken = null;
        var address = Normalize(remoteAddress);
        lock (_gate)
        {
            RemoveExpiredSessionsLocked(_now());
            if (!string.IsNullOrWhiteSpace(sessionToken) &&
                _sessions.TryGetValue(sessionToken, out var session) &&
                session.Address.Equals(address))
            {
                return true;
            }

            if (!_deviceTokenStore.Validate(deviceToken))
                return false;

            issuedSessionToken = CreateToken();
            _sessions[issuedSessionToken] = new SessionEntry(address, _now() + SessionLifetime);
            return true;
        }
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
            _recentFailures.Clear();
        }
        RotateAccessCodes();
    }

    internal static IPAddress Normalize(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

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

    private sealed record SessionEntry(IPAddress Address, DateTimeOffset ExpiresUtc);
    private sealed record FailureState(int Count, DateTimeOffset LockedUntilUtc);
}
