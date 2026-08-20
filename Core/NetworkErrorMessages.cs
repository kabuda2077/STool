using System.Net;
using System.Net.Http;

namespace STool.Core;

internal static class NetworkErrorMessages
{
    public static string FromStatus(HttpStatusCode statusCode, string? body = null)
    {
        var detail = string.IsNullOrWhiteSpace(body) ? string.Empty : $"：{Trim(body, 240)}";
        return statusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "认证失败，请检查 API Key 和访问权限。",
            HttpStatusCode.TooManyRequests => "请求过于频繁，服务已限流，请稍后重试。",
            >= HttpStatusCode.InternalServerError => $"服务暂时不可用（{(int)statusCode}）{detail}",
            _ => $"请求失败（{(int)statusCode} {statusCode}）{detail}"
        };
    }

    public static string FromException(Exception exception, CancellationToken cancellationToken = default)
    {
        return exception switch
        {
            OperationCanceledException when cancellationToken.IsCancellationRequested => "操作已取消。",
            TaskCanceledException => "请求超时，请检查网络后重试。",
            HttpRequestException => "网络连接失败，请检查网络或服务地址。",
            _ => exception.Message
        };
    }

    private static string Trim(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + "...";
}
