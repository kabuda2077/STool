using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;

namespace STool.Core;

/// <summary>Resolves short OpenAI-compatible base URLs into concrete API endpoints.</summary>
internal static class AiApiEndpointResolver
{
    private const string ChatCompletionsSuffix = "/chat/completions";
    private const string ResponsesSuffix = "/responses";
    private const string ModelsSuffix = "/models";

    public static IReadOnlyList<string> ResolveChatCompletionCandidates(string apiUrl)
    {
        var uri = ParseApiUrl(apiUrl);
        var path = NormalizePath(uri.AbsolutePath);

        if (path.EndsWith(ResponsesSuffix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("当前版本暂不支持 Responses API，请使用 Chat Completions 地址。");
        }

        var candidatePaths = new List<string>();
        if (path.EndsWith(ChatCompletionsSuffix, StringComparison.OrdinalIgnoreCase))
        {
            candidatePaths.Add(path);
        }
        else if (path.EndsWith(ModelsSuffix, StringComparison.OrdinalIgnoreCase))
        {
            candidatePaths.Add(path[..^ModelsSuffix.Length] + ChatCompletionsSuffix);
        }
        else if (string.IsNullOrEmpty(path))
        {
            candidatePaths.Add("/v1/chat/completions");
            candidatePaths.Add(ChatCompletionsSuffix);
        }
        else if (path.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            candidatePaths.Add(path + ChatCompletionsSuffix);
            candidatePaths.Add(path[..^3] + ChatCompletionsSuffix);
        }
        else
        {
            candidatePaths.Add(path + ChatCompletionsSuffix);
        }

        return candidatePaths
            .Select(pathCandidate => BuildUrl(uri, pathCandidate))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static string ResolvePrimaryChatCompletionUrl(string apiUrl)
    {
        return ResolveChatCompletionCandidates(apiUrl)[0];
    }

    public static IReadOnlyList<string> ResolveModelsCandidates(string apiUrl)
    {
        var uri = ParseApiUrl(apiUrl);
        var path = NormalizePath(uri.AbsolutePath);
        if (path.EndsWith(ResponsesSuffix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("当前版本暂不支持 Responses API，请使用 Chat Completions 地址。");
        }

        if (path.EndsWith(ModelsSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return new[] { BuildUrl(uri, path) };
        }

        return ResolveChatCompletionCandidates(apiUrl)
            .Select(candidate =>
            {
                var candidateUri = new Uri(candidate);
                var candidatePath = NormalizePath(candidateUri.AbsolutePath);
                return BuildUrl(candidateUri, candidatePath[..^ChatCompletionsSuffix.Length] + ModelsSuffix);
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static bool ShouldTryNextEndpoint(HttpStatusCode statusCode, string responseBody)
    {
        if (statusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
        {
            return true;
        }

        if (statusCode != HttpStatusCode.BadRequest)
        {
            return false;
        }

        var body = responseBody.ToLowerInvariant();
        return body.Contains("invalid url", StringComparison.Ordinal) ||
               body.Contains("unknown endpoint", StringComparison.Ordinal) ||
               body.Contains("route not found", StringComparison.Ordinal) ||
               body.Contains("no route", StringComparison.Ordinal);
    }

    private static Uri ParseApiUrl(string apiUrl)
    {
        var normalized = apiUrl.Trim();
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrWhiteSpace(uri.Host))
        {
            throw new InvalidOperationException("请输入以 http:// 或 https:// 开头的有效 API 地址。");
        }

        return uri;
    }

    private static string NormalizePath(string path)
    {
        var normalized = path.Trim().TrimEnd('/');
        return normalized == "/" ? string.Empty : normalized;
    }

    private static string BuildUrl(Uri source, string path)
    {
        var builder = new UriBuilder(source)
        {
            Path = path,
            Fragment = string.Empty
        };
        return builder.Uri.AbsoluteUri;
    }
}
