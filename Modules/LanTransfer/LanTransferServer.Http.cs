using System.IO;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace STool.Modules.LanTransfer;

/// <summary>LanTransferServer 的 HTTP 辅助方法：JSON、静态资源、Cookie、Range 与下载头。</summary>
internal sealed partial class LanTransferServer
{
    private const string SessionCookiePath = "/";

    /// <summary>设备令牌只发往会话接口，普通请求不再携带长期有效的凭据。</summary>
    private const string DeviceCookiePath = "/api/session";

    private static readonly TimeSpan SessionCookieLifetime = TimeSpan.FromHours(12);
    private static readonly TimeSpan DeviceCookieLifetime = TimeSpan.FromDays(90);

    private static Task<T?> ReadJsonAsync<T>(HttpListenerRequest request, CancellationToken cancellationToken) =>
        ReadJsonAsync<T>(request.InputStream, request.ContentLength64, cancellationToken);

    internal static async Task<T?> ReadJsonAsync<T>(
        Stream stream,
        long contentLength,
        CancellationToken cancellationToken = default)
    {
        if (contentLength is < 0 or > 64 * 1024)
            throw new InvalidDataException("请求内容过大。 ");

        try
        {
            return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("请求内容格式无效。 ", ex);
        }
    }

    private static async Task WriteAssetAsync(HttpListenerResponse response, string fileName, string contentType, CancellationToken cancellationToken)
    {
        var content = LanTransferWebAssets.Read(fileName);
        if (content == null)
        {
            await WriteErrorAsync(response, 404, "网页资源不存在。 ", cancellationToken);
            return;
        }
        response.ContentType = contentType;
        response.ContentLength64 = content.Length;
        response.Headers["Cache-Control"] = "no-store";
        await response.OutputStream.WriteAsync(content, cancellationToken);
    }

    private static async Task WriteJsonAsync<T>(HttpListenerResponse response, T value, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        response.ContentType = "application/json; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, cancellationToken);
    }

    private static Task WriteErrorAsync(HttpListenerResponse response, int statusCode, string message, CancellationToken cancellationToken)
    {
        response.StatusCode = statusCode;
        return WriteJsonAsync(response, new { error = message }, cancellationToken);
    }

    private static string? GetCookie(HttpListenerRequest request, string name) => request.Cookies[name]?.Value;

    // 每个 Cookie 单独一行 Set-Cookie：HttpListener 的 AppendCookie 会把多个 Cookie
    // 用逗号合并进同一个头，而 Expires 里本身带逗号，浏览器会解析错。
    private static void SetCookie(HttpListenerResponse response, string name, string value, string path, TimeSpan lifetime)
    {
        response.AppendHeader("Set-Cookie", BuildSetCookie(name, value, path, DateTimeOffset.UtcNow + lifetime));
    }

    private static void ExpireCookie(HttpListenerResponse response, string name, string path)
    {
        response.AppendHeader("Set-Cookie", BuildSetCookie(name, string.Empty, path, DateTimeOffset.UnixEpoch));
    }

    internal static string BuildSetCookie(string name, string value, string path, DateTimeOffset expires)
    {
        var maxAge = Math.Max(0, (long)(expires - DateTimeOffset.UtcNow).TotalSeconds);
        return $"{name}={value}; Path={path}; Expires={expires.UtcDateTime:R}; Max-Age={maxAge}; HttpOnly; SameSite=Lax";
    }

    internal static bool TryParseRange(string header, long length, out long start, out long end)
    {
        start = 0;
        end = Math.Max(0, length - 1);
        if (length <= 0 || !header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase) || header.Contains(','))
            return false;

        var parts = header[6..].Split('-', 2);
        if (parts.Length != 2)
            return false;

        if (string.IsNullOrWhiteSpace(parts[0]))
        {
            if (!long.TryParse(parts[1], out var suffixLength) || suffixLength <= 0)
                return false;
            suffixLength = Math.Min(suffixLength, length);
            start = length - suffixLength;
            end = length - 1;
            return true;
        }

        if (!long.TryParse(parts[0], out start) || start < 0 || start >= length)
            return false;
        if (string.IsNullOrWhiteSpace(parts[1]))
        {
            end = length - 1;
            return true;
        }
        return long.TryParse(parts[1], out end) && end >= start && end < length;
    }

    private static string BuildContentDisposition(string fileName)
    {
        var ascii = new string(fileName.Select(ch => ch is >= ' ' and <= '~' && ch != '"' ? ch : '_').ToArray());
        return $"attachment; filename=\"{ascii}\"; filename*=UTF-8''{Uri.EscapeDataString(fileName)}";
    }

    private static string GetContentType(string extension) => extension.ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".pdf" => "application/pdf",
        ".txt" => "text/plain; charset=utf-8",
        ".json" => "application/json",
        ".zip" => "application/zip",
        _ => "application/octet-stream"
    };

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    private static void RedirectToTransferDownload(HttpListenerResponse response, string transferId)
    {
        response.StatusCode = 302;
        response.RedirectLocation = $"/api/transfers/{transferId}/download";
    }

    private static string GetRequestOwner(HttpListenerRequest request) =>
        GetCookie(request, LanTransferAuthService.SessionCookieName) ??
        GetCookie(request, LanTransferAuthService.DeviceCookieName) ??
        request.RemoteEndPoint?.Address?.ToString() ?? "unknown";

    private static string GetCommonLocation(IReadOnlyList<string> paths)
    {
        if (paths.Count == 1)
            return Path.GetFullPath(paths[0]);

        var directories = paths.Select(path =>
            Directory.Exists(path)
                ? Path.GetFullPath(path)
                : Path.GetDirectoryName(Path.GetFullPath(path)) ?? Path.GetPathRoot(path) ?? path).ToArray();
        var common = directories[0].TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        foreach (var directory in directories.Skip(1))
        {
            while (!directory.Equals(common, StringComparison.OrdinalIgnoreCase) &&
                   !directory.StartsWith(common + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                var parent = Path.GetDirectoryName(common);
                if (string.IsNullOrWhiteSpace(parent))
                    return Path.GetPathRoot(common) ?? common;
                common = parent;
            }
        }
        return common;
    }

    private static long AddSaturating(long left, long right) =>
        left > long.MaxValue - right ? long.MaxValue : left + right;
}
