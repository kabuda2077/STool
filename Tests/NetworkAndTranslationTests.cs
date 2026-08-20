using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using STool.Core;
using STool.Modules.LanTransfer;
using STool.Modules.Translation;
using Xunit;

namespace STool.Tests;

public class NetworkAndTranslationTests
{
    [Fact]
    public void GoogleResponseParser_CombinesSegmentsAndDetectsLanguage()
    {
        using var document = JsonDocument.Parse("[[[\"你\",\"you\"],[\"好\",\" good\"]],null,\"en\"]");

        var parsed = GoogleTranslationService.ParseResponse(document.RootElement, "auto");

        Assert.Equal("你好", parsed.TranslatedText);
        Assert.Equal("en", parsed.SourceLanguage);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("[null]")]
    public void GoogleResponseParser_InvalidShape_Throws(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Throws<InvalidOperationException>(() =>
            GoogleTranslationService.ParseResponse(document.RootElement, "auto"));
    }

    [Fact]
    public void GoogleResponseParser_EmptyTranslation_IsObservable()
    {
        using var document = JsonDocument.Parse("[[[null,\"source\"]],null,\"en\"]");

        var parsed = GoogleTranslationService.ParseResponse(document.RootElement, "auto");

        Assert.Equal(string.Empty, parsed.TranslatedText);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "认证失败")]
    [InlineData(HttpStatusCode.TooManyRequests, "限流")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "暂时不可用")]
    public void NetworkErrorMessages_ClassifiesStatus(HttpStatusCode status, string expected)
    {
        Assert.Contains(expected, NetworkErrorMessages.FromStatus(status, "details"));
    }

    [Fact]
    public void NetworkErrorMessages_ClassifiesNetworkFailure()
    {
        var message = NetworkErrorMessages.FromException(new HttpRequestException("socket"));
        Assert.Contains("网络连接失败", message);
    }

    [Fact]
    public void LanTransferLifecycleState_ContainsExpectedStates()
    {
        var names = Enum.GetNames<LanTransferWindow.ServerLifecycleState>();

        Assert.Equal(
            ["Stopped", "Starting", "PermissionRequired", "Running", "Stopping", "Failed"],
            names);
    }
}
