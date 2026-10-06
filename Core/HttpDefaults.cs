using System;
using System.Net;
using System.Net.Http;

namespace STool.Core;

/// <summary>
/// 网络请求统一默认值。所有服务共用一个连接池，复用 TCP/TLS 连接；
/// 超时作为兜底，保证即使调用方未传 CancellationToken 请求也不会无限期挂起。
/// </summary>
public static class HttpDefaults
{
    public static readonly TimeSpan NetworkTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// 进程级共享 HttpClient。认证信息和请求头必须放在 HttpRequestMessage 上，
    /// 不要写入 DefaultRequestHeaders，否则会串到其他服务的请求里。
    /// </summary>
    public static HttpClient Shared { get; } = new(new SocketsHttpHandler
    {
        // 定期重建连接，让 DNS 变化（换网络、代理切换）能生效。
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        // API 凭据仅来自当前请求，不在共享客户端里保存服务端 Cookie。
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(15),
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
    })
    {
        Timeout = NetworkTimeout
    };
}
