using STool.Modules.Ocr;
using Xunit;

namespace STool.Tests;

public class AiVisionOcrServiceTests
{
    [Fact]
    public void Prompt_RequiresFaithfulTranscription()
    {
        Assert.Contains("Preserve the original language", AiVisionOcrService.OcrPrompt);
        Assert.Contains("Do not translate", AiVisionOcrService.OcrPrompt);
        Assert.Contains("line breaks", AiVisionOcrService.OcrPrompt);
        Assert.Contains("do not", AiVisionOcrService.OcrPrompt, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseApiResponse_StringContent_ReturnsText()
    {
        const string json = """
            {
              "choices": [
                {
                  "finish_reason": "stop",
                  "message": { "content": "  first line\nsecond line  " }
                }
              ]
            }
            """;

        var result = AiVisionOcrService.ParseApiResponse(json);

        Assert.True(result.Success);
        Assert.Equal("first line\nsecond line", result.FullText);
        Assert.Single(result.TextBlocks);
        Assert.Equal(result.FullText, result.TextBlocks[0].Text);
    }

    [Fact]
    public void ParseApiResponse_ContentPartArray_JoinsTextParts()
    {
        const string json = """
            {
              "choices": [
                {
                  "finish_reason": "stop",
                  "message": {
                    "content": [
                      { "type": "text", "text": "first line" },
                      { "type": "text", "text": "second line" }
                    ]
                  }
                }
              ]
            }
            """;

        var result = AiVisionOcrService.ParseApiResponse(json);

        Assert.True(result.Success);
        Assert.Equal($"first line{System.Environment.NewLine}second line", result.FullText);
    }

    [Fact]
    public void ParseApiResponse_LengthFinishReason_RejectsPartialText()
    {
        const string json = """
            {
              "choices": [
                {
                  "finish_reason": "length",
                  "message": { "content": "partial text" }
                }
              ]
            }
            """;

        var result = AiVisionOcrService.ParseApiResponse(json);

        Assert.False(result.Success);
        Assert.Contains("长度上限", result.ErrorMessage);
    }

    [Fact]
    public void ParseApiResponse_EmptyContent_ReturnsFailure()
    {
        const string json = """
            {
              "choices": [
                {
                  "finish_reason": "stop",
                  "message": { "content": "   " }
                }
              ]
            }
            """;

        var result = AiVisionOcrService.ParseApiResponse(json);

        Assert.False(result.Success);
        Assert.Contains("未识别到", result.ErrorMessage);
    }

    [Fact]
    public void ParseApiResponse_MalformedResponse_ReturnsFailure()
    {
        var result = AiVisionOcrService.ParseApiResponse("not-json");

        Assert.False(result.Success);
        Assert.Contains("JSON", result.ErrorMessage);
    }

    [Fact]
    public void ParseApiResponse_ContentFilter_ReturnsFailure()
    {
        const string json = """
            {
              "choices": [
                {
                  "finish_reason": "content_filter",
                  "message": { "content": "" }
                }
              ]
            }
            """;

        var result = AiVisionOcrService.ParseApiResponse(json);

        Assert.False(result.Success);
        Assert.Contains("安全策略", result.ErrorMessage);
    }
}
