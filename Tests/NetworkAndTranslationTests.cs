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

    [Fact]
    public void GoogleChunks_ShortTextStaysInOneRequest()
    {
        var chunks = GoogleTranslationService.SplitIntoChunks("line one\nline two", 5000);

        var chunk = Assert.Single(chunks);
        Assert.Equal("line one\nline two", chunk.Text);
    }

    [Fact]
    public void GoogleChunks_LongMultilineTextIsSplitWithinEncodedLimitAndRejoins()
    {
        var lines = Enumerable.Range(0, 400).Select(index => $"第 {index} 行中文内容 with English words").ToArray();
        var text = string.Join("\n", lines);

        var chunks = GoogleTranslationService.SplitIntoChunks(text, 2000);

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, chunk => Assert.True(GoogleTranslationService.EncodedLength(chunk.Text) <= 2000));
        Assert.Equal(text, string.Concat(chunks.Select(chunk => chunk.SeparatorBefore + chunk.Text)));
    }

    [Fact]
    public void GoogleChunks_SingleHugeLineIsSplitAtSpaces()
    {
        var text = string.Join(" ", Enumerable.Repeat("word", 3000));

        var chunks = GoogleTranslationService.SplitIntoChunks(text, 1000);

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, chunk =>
        {
            Assert.True(GoogleTranslationService.EncodedLength(chunk.Text) <= 1000);
            Assert.DoesNotContain("wor d", chunk.Text);
        });
        Assert.Equal(3000, chunks.Sum(chunk => chunk.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length));
    }

    [Fact]
    public void TencentSigner_BuildsTc3AuthorizationHeader()
    {
        var header = TencentCloudSigner.BuildAuthorization(
            "AKIDEXAMPLE",
            "secret",
            "tmt",
            "tmt.tencentcloudapi.com",
            "{}",
            1_700_000_000,
            "2023-11-14");

        Assert.StartsWith("TC3-HMAC-SHA256 Credential=AKIDEXAMPLE/2023-11-14/tmt/tc3_request, SignedHeaders=content-type;host, Signature=", header);
        var signature = header[(header.LastIndexOf('=') + 1)..];
        Assert.Equal(64, signature.Length);
        Assert.Equal(header, TencentCloudSigner.BuildAuthorization(
            "AKIDEXAMPLE", "secret", "tmt", "tmt.tencentcloudapi.com", "{}", 1_700_000_000, "2023-11-14"));
        Assert.NotEqual(header, TencentCloudSigner.BuildAuthorization(
            "AKIDEXAMPLE", "other-secret", "tmt", "tmt.tencentcloudapi.com", "{}", 1_700_000_000, "2023-11-14"));
    }

    [Theory]
    [InlineData("Debug", Serilog.Events.LogEventLevel.Debug)]
    [InlineData("warning", Serilog.Events.LogEventLevel.Warning)]
    [InlineData("nonsense", Serilog.Events.LogEventLevel.Information)]
    [InlineData(null, Serilog.Events.LogEventLevel.Information)]
    public void LogLevel_ParsesConfigValueWithFallback(string? value, Serilog.Events.LogEventLevel expected)
    {
        Assert.Equal(expected, AppLogging.ParseLevel(value));
    }

    [Theory]
    [InlineData("\"C:\\Tools\\STool\\STool.exe\"", "C:\\Tools\\STool\\STool.exe", true)]
    [InlineData("C:\\Tools\\STool\\STool.exe", "c:\\tools\\stool\\STOOL.EXE", true)]
    [InlineData("\"D:\\Old\\STool.exe\"", "C:\\Tools\\STool\\STool.exe", false)]
    public void AutoStart_ComparesRegisteredCommandWithCurrentPath(string registered, string current, bool expected)
    {
        Assert.Equal(expected, STool.Views.Settings.GeneralSettingsPanel.IsSamePath(registered, current));
    }
}
