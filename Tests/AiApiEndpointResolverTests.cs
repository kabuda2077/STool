using System.Net;
using STool.Core;
using Xunit;

namespace STool.Tests;

public class AiApiEndpointResolverTests
{
    [Theory]
    [InlineData("https://muyuan.do", "https://muyuan.do/v1/chat/completions")]
    [InlineData("https://muyuan.do/", "https://muyuan.do/v1/chat/completions")]
    [InlineData("https://muyuan.do/v1", "https://muyuan.do/v1/chat/completions")]
    [InlineData("https://muyuan.do/v1/", "https://muyuan.do/v1/chat/completions")]
    [InlineData("https://muyuan.do/openai/v1", "https://muyuan.do/openai/v1/chat/completions")]
    [InlineData("https://muyuan.do/api/openai", "https://muyuan.do/api/openai/chat/completions")]
    public void ResolvePrimaryChatCompletionUrl_BaseAddress_CompletesExpectedPath(string input, string expected)
    {
        Assert.Equal(expected, AiApiEndpointResolver.ResolvePrimaryChatCompletionUrl(input));
    }

    [Fact]
    public void ResolveChatCompletionCandidates_RootAddress_ProvidesNoV1Fallback()
    {
        var candidates = AiApiEndpointResolver.ResolveChatCompletionCandidates("https://muyuan.do");

        Assert.Equal(new[]
        {
            "https://muyuan.do/v1/chat/completions",
            "https://muyuan.do/chat/completions"
        }, candidates);
    }

    [Fact]
    public void ResolveChatCompletionCandidates_CompleteAddress_IsPreserved()
    {
        var candidates = AiApiEndpointResolver.ResolveChatCompletionCandidates(
            "https://muyuan.do/v1/chat/completions?channel=one");

        Assert.Equal(new[] { "https://muyuan.do/v1/chat/completions?channel=one" }, candidates);
    }

    [Fact]
    public void ResolveChatCompletionCandidates_ResponsesAddress_IsRejected()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            AiApiEndpointResolver.ResolveChatCompletionCandidates("https://muyuan.do/v1/responses"));

        Assert.Contains("Responses API", error.Message);
    }

    [Fact]
    public void ResolveModelsCandidates_RootAddress_MatchesChatCandidates()
    {
        var candidates = AiApiEndpointResolver.ResolveModelsCandidates("https://muyuan.do");

        Assert.Equal(new[]
        {
            "https://muyuan.do/v1/models",
            "https://muyuan.do/models"
        }, candidates);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "", true)]
    [InlineData(HttpStatusCode.MethodNotAllowed, "", true)]
    [InlineData(HttpStatusCode.BadRequest, "Invalid URL", true)]
    [InlineData(HttpStatusCode.Unauthorized, "", false)]
    [InlineData(HttpStatusCode.Forbidden, "route not found", false)]
    [InlineData(HttpStatusCode.TooManyRequests, "", false)]
    public void ShouldTryNextEndpoint_OnlyRetriesPathErrors(HttpStatusCode status, string body, bool expected)
    {
        Assert.Equal(expected, AiApiEndpointResolver.ShouldTryNextEndpoint(status, body));
    }
}
