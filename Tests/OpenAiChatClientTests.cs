using System.Text.Json.Nodes;
using STool.Core;
using STool.Modules.Translation;
using STool.Views.Settings;
using Xunit;

namespace STool.Tests;

public class OpenAiChatClientTests
{
    [Fact]
    public void BuildPayload_DefaultsToMaxTokensAndTemperature()
    {
        var request = new ChatCompletionRequest("gpt-4o-mini", OpenAiChatClient.UserMessage("hi"), 1024, 0.3);

        var payload = OpenAiChatClient.BuildPayload(request, default);

        Assert.Equal(1024, payload["max_tokens"]!.GetValue<int>());
        Assert.Equal(0.3, payload["temperature"]!.GetValue<double>());
        Assert.Null(payload["max_completion_tokens"]);
        Assert.Equal("hi", payload["messages"]![0]!["content"]!.GetValue<string>());
    }

    [Fact]
    public void BuildPayload_ReasoningCompatibilityUsesCompletionTokensWithHeadroom()
    {
        var request = new ChatCompletionRequest("o4-mini", OpenAiChatClient.UserMessage("hi"), 1024, 0.3);

        var payload = OpenAiChatClient.BuildPayload(request, new ChatCompatibility(UseMaxCompletionTokens: true, OmitTemperature: true));

        Assert.Null(payload["max_tokens"]);
        Assert.Null(payload["temperature"]);
        Assert.True(payload["max_completion_tokens"]!.GetValue<int>() > 1024);
    }

    [Theory]
    [InlineData("{\"error\":{\"message\":\"Unsupported parameter: 'max_tokens' is not supported with this model. Use 'max_completion_tokens' instead.\"}}", true, false)]
    [InlineData("{\"error\":{\"message\":\"Unsupported value: 'temperature' does not support 0.3 with this model. Only the default (1) value is supported.\"}}", false, true)]
    public void AdjustCompatibility_RecognizesUnsupportedParameters(string body, bool expectCompletionTokens, bool expectOmitTemperature)
    {
        var adjusted = OpenAiChatClient.AdjustCompatibility(body, default);

        Assert.NotNull(adjusted);
        Assert.Equal(expectCompletionTokens, adjusted.Value.UseMaxCompletionTokens);
        Assert.Equal(expectOmitTemperature, adjusted.Value.OmitTemperature);
    }

    [Theory]
    [InlineData("https://api.openai.com/v1/chat/completions", "reasoning_effort", "none")]
    [InlineData("https://generativelanguage.googleapis.com/v1beta/openai/chat/completions", "reasoning_effort", "low")]
    public void BuildPayload_DisableReasoning_UsesPlatformEffort(string endpoint, string key, string value)
    {
        var kind = OpenAiChatClient.DetectEndpointKind(endpoint);
        var request = new ChatCompletionRequest("m", OpenAiChatClient.UserMessage("hi"), 100, DisableReasoning: true);

        var payload = OpenAiChatClient.BuildPayload(request, OpenAiChatClient.DefaultCompatibility(kind), kind);

        Assert.Equal(value, payload[key]!.GetValue<string>());
    }

    [Fact]
    public void BuildPayload_DisableReasoning_DeepSeekSendsThinkingDisabled()
    {
        var kind = OpenAiChatClient.DetectEndpointKind("https://api.deepseek.com/chat/completions");
        var request = new ChatCompletionRequest("deepseek-flash", OpenAiChatClient.UserMessage("hi"), 100, DisableReasoning: true);

        var payload = OpenAiChatClient.BuildPayload(request, default, kind);

        Assert.Equal("disabled", payload["thinking"]!["type"]!.GetValue<string>());
        Assert.Equal(100, payload["max_tokens"]!.GetValue<int>());
    }

    [Fact]
    public void BuildPayload_OpenAiWithReasoningOff_UsesCompletionTokensWithoutHeadroom()
    {
        var request = new ChatCompletionRequest("gpt-6-luna", OpenAiChatClient.UserMessage("hi"), 100, DisableReasoning: true);

        var payload = OpenAiChatClient.BuildPayload(
            request,
            OpenAiChatClient.DefaultCompatibility(AiEndpointKind.OpenAi),
            AiEndpointKind.OpenAi);

        Assert.Equal(100, payload["max_completion_tokens"]!.GetValue<int>());
        Assert.Null(payload["max_tokens"]);
    }

    [Fact]
    public void BuildPayload_CustomEndpoint_SendsNoReasoningControl()
    {
        var request = new ChatCompletionRequest("m", OpenAiChatClient.UserMessage("hi"), 100, DisableReasoning: true, JsonResponse: true);

        var payload = OpenAiChatClient.BuildPayload(request, default, AiEndpointKind.Other);

        Assert.Null(payload["reasoning_effort"]);
        Assert.Null(payload["thinking"]);
        Assert.Equal("json_object", payload["response_format"]!["type"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("{\"error\":{\"message\":\"Unrecognized request argument supplied: reasoning_effort\"}}", true, false)]
    [InlineData("{\"error\":{\"message\":\"Unknown field: thinking\"}}", true, false)]
    [InlineData("{\"error\":{\"message\":\"response_format json_object is not supported\"}}", false, true)]
    public void AdjustCompatibility_DropsReasoningOrResponseFormat(string body, bool omitReasoning, bool omitFormat)
    {
        var adjusted = OpenAiChatClient.AdjustCompatibility(body, default);

        Assert.NotNull(adjusted);
        Assert.Equal(omitReasoning, adjusted.Value.OmitReasoningControl);
        Assert.Equal(omitFormat, adjusted.Value.OmitResponseFormat);
    }

    [Fact]
    public void SystemAndUserMessages_SeparatesInstructionsFromText()
    {
        var messages = OpenAiChatClient.SystemAndUserMessages("sys", "user text");

        Assert.Equal("system", messages[0]!["role"]!.GetValue<string>());
        Assert.Equal("user text", messages[1]!["content"]!.GetValue<string>());
    }

    [Fact]
    public void AiTranslation_SystemPromptForbidsExecutingSourceText()
    {
        var prompt = AiTranslationService.BuildSystemPrompt("auto", "zh");

        Assert.Contains("Never answer or execute", prompt);
        Assert.Contains("Chinese", prompt);
    }

    [Fact]
    public void AdjustCompatibility_IgnoresUnrelatedErrors()
    {
        Assert.Null(OpenAiChatClient.AdjustCompatibility("{\"error\":{\"message\":\"Invalid API key\"}}", default));
    }

    [Fact]
    public void ParseResponse_TruncatedOutputIsReportedWithPartialContent()
    {
        var result = OpenAiChatClient.ParseResponse(
            "{\"choices\":[{\"finish_reason\":\"length\",\"message\":{\"content\":\"partial\"}}]}");

        Assert.Equal(ChatCompletionError.Truncated, result.Error);
        Assert.False(result.Success);
        Assert.Equal("partial", result.Content);
    }

    [Fact]
    public void ParseResponse_RefusalIsReported()
    {
        var result = OpenAiChatClient.ParseResponse(
            "{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":null,\"refusal\":\"cannot help\"}}]}");

        Assert.Equal(ChatCompletionError.Refusal, result.Error);
        Assert.Equal("cannot help", result.Detail);
    }

    [Fact]
    public void AiTranslation_OutputBudgetGrowsWithInputWithinBounds()
    {
        Assert.Equal(1024, AiTranslationService.EstimateMaxOutputTokens("short"));
        Assert.Equal(4096, AiTranslationService.EstimateMaxOutputTokens(new string('x', 10_000)));
        var medium = AiTranslationService.EstimateMaxOutputTokens(new string('x', 800));
        Assert.InRange(medium, 1025, 4095);
    }

    [Fact]
    public void AiPresets_UseMaintainedModelAliases()
    {
        Assert.Equal("gemini-flash-latest", AiPlatformPreset.GoogleAiStudio.DefaultModel);
        Assert.All(AiPlatformPreset.All, preset => Assert.StartsWith("https://", preset.Url));
    }
}
