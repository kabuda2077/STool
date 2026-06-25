using System;
using System.Net.Http;

namespace STool.Core;

/// <summary>
/// 网络请求统一默认值。集中管理超时,避免各服务用 HttpClient 默认的 100s
/// 导致慢网络时翻译/OCR 长时间无响应、用户无法中断。
/// </summary>
public static class HttpDefaults
{
    /// <summary>
    /// 网络请求超时上限。作为兜底:即使调用方未传 CancellationToken,
    /// 也保证请求不会无限期挂起。
    /// </summary>
    public static readonly TimeSpan NetworkTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// 进程级共享 HttpClient,供无需独立凭据/请求头状态的偶发调用复用
    /// (如拉取模型列表),避免频繁 new/Dispose 造成的 socket 句柄浪费。
    /// 认证信息须放在 HttpRequestMessage 上,不要写入 DefaultRequestHeaders。
    /// </summary>
    public static HttpClient Shared { get; } = CreateClient();

    public static HttpClient CreateClient()
    {
        return new HttpClient { Timeout = NetworkTimeout };
    }
}
