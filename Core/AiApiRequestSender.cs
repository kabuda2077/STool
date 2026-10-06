using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace STool.Core;

internal sealed record AiApiHttpResult(
    string Endpoint,
    HttpStatusCode StatusCode,
    string? ReasonPhrase,
    string Body,
    long HeadersElapsedMilliseconds,
    long TotalElapsedMilliseconds)
{
    public bool IsSuccessStatusCode => (int)StatusCode is >= 200 and <= 299;
}

internal static class AiApiRequestSender
{
    public static async Task<AiApiHttpResult> SendAsync(
        HttpClient httpClient,
        IReadOnlyList<string> candidates,
        Func<string, HttpRequestMessage> createRequest,
        CancellationToken cancellationToken = default,
        TimeSpan? requestTimeout = null)
    {
        // ResponseHeadersRead 让 HttpClient.Timeout 只覆盖响应头；读取正文也必须有截止时间。
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(requestTimeout ?? HttpDefaults.NetworkTimeout);
        var requestToken = deadline.Token;

        if (candidates.Count == 0)
        {
            throw new InvalidOperationException("没有可用的 AI API 请求地址。");
        }

        AiApiHttpResult? lastResult = null;
        for (var i = 0; i < candidates.Count; i++)
        {
            var endpoint = candidates[i];
            var stopwatch = Stopwatch.StartNew();
            using var request = createRequest(endpoint);
            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                requestToken).ConfigureAwait(false);
            var headersElapsed = stopwatch.ElapsedMilliseconds;
            var body = await response.Content.ReadAsStringAsync(requestToken).ConfigureAwait(false);
            lastResult = new AiApiHttpResult(
                endpoint,
                response.StatusCode,
                response.ReasonPhrase,
                body,
                headersElapsed,
                stopwatch.ElapsedMilliseconds);

            if (lastResult.IsSuccessStatusCode ||
                i == candidates.Count - 1 ||
                !AiApiEndpointResolver.ShouldTryNextEndpoint(response.StatusCode, body))
            {
                return lastResult;
            }
        }

        return lastResult!;
    }
}
