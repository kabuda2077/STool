using System;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace STool.Core;

/// <summary>腾讯云 API 3.0（TC3-HMAC-SHA256）签名与请求构造，OCR 与机器翻译共用。</summary>
internal static class TencentCloudSigner
{
    private const string Algorithm = "TC3-HMAC-SHA256";
    private const string SignedHeaders = "content-type;host";
    private const string DefaultRegion = "ap-guangzhou";

    public static HttpRequestMessage CreateRequest(
        string secretId,
        string secretKey,
        string service,
        string host,
        string action,
        string version,
        string payloadJson,
        DateTimeOffset now)
    {
        var timestamp = now.ToUnixTimeSeconds();
        var date = now.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var authorization = BuildAuthorization(secretId, secretKey, service, host, payloadJson, timestamp, date);

        var request = new HttpRequestMessage(HttpMethod.Post, $"https://{host}/")
        {
            Content = new StringContent(payloadJson, Encoding.UTF8, "application/json")
        };

        // 签名里的 content-type 不含 charset，这里改回纯 application/json 与签名一致。
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        // Authorization 含 "/" 等非 token 字符，.NET 的强校验会抛 FormatException，只能绕过校验原样发送。
        request.Headers.TryAddWithoutValidation("Authorization", authorization);
        request.Headers.TryAddWithoutValidation("X-TC-Action", action);
        request.Headers.TryAddWithoutValidation("X-TC-Version", version);
        request.Headers.TryAddWithoutValidation("X-TC-Timestamp", timestamp.ToString(CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation("X-TC-Region", DefaultRegion);
        return request;
    }

    internal static string BuildAuthorization(
        string secretId,
        string secretKey,
        string service,
        string host,
        string payloadJson,
        long timestamp,
        string date)
    {
        var canonicalRequest =
            $"POST\n/\n\ncontent-type:application/json\nhost:{host}\n\n{SignedHeaders}\n{Sha256Hex(payloadJson)}";
        var credentialScope = $"{date}/{service}/tc3_request";
        var stringToSign = $"{Algorithm}\n{timestamp}\n{credentialScope}\n{Sha256Hex(canonicalRequest)}";

        var secretDate = HmacSha256(Encoding.UTF8.GetBytes($"TC3{secretKey}"), Encoding.UTF8.GetBytes(date));
        var secretService = HmacSha256(secretDate, Encoding.UTF8.GetBytes(service));
        var secretSigning = HmacSha256(secretService, Encoding.UTF8.GetBytes("tc3_request"));
        var signature = Convert.ToHexString(HmacSha256(secretSigning, Encoding.UTF8.GetBytes(stringToSign))).ToLowerInvariant();

        return $"{Algorithm} Credential={secretId}/{credentialScope}, SignedHeaders={SignedHeaders}, Signature={signature}";
    }

    private static string Sha256Hex(string data) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(data))).ToLowerInvariant();

    private static byte[] HmacSha256(byte[] key, byte[] data) => HMACSHA256.HashData(key, data);
}
