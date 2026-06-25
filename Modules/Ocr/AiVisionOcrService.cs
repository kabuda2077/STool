using System;
using System.Drawing;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using STool.Core;

namespace STool.Modules.Ocr;

/// <summary>
/// AI Vision OCR 服务（OpenAI/Claude）
/// </summary>
public class AiVisionOcrService : IOcrService
{
    private readonly string _apiUrl;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly HttpClient _httpClient;

    public AiVisionOcrService(string apiUrlEncrypted, string apiKeyEncrypted, string model)
    {
        _apiUrl = SecureStorage.Decrypt(apiUrlEncrypted);
        _apiKey = SecureStorage.Decrypt(apiKeyEncrypted);
        _model = model;
        _httpClient = HttpDefaults.CreateClient();
    }

    public bool IsAvailable()
    {
        return !string.IsNullOrEmpty(_apiUrl) && !string.IsNullOrEmpty(_apiKey);
    }

    public async Task<OcrResult> RecognizeAsync(Bitmap image, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable())
        {
            return new OcrResult
            {
                Success = false,
                ErrorMessage = "AI API credentials not configured",
                Provider = "AI Vision"
            };
        }

        try
        {
            // 转换图片为 Base64
            string imageBase64;
            using (var ms = new MemoryStream())
            {
                image.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                imageBase64 = Convert.ToBase64String(ms.ToArray());
            }

            // 构建 OpenAI 兼容请求
            var payload = new
            {
                model = _model,
                messages = new[]
                {
                    new
                    {
                        role = "user",
                        content = new object[]
                        {
                            new { type = "text", text = "Extract all text from this image. Return only the extracted text, without any additional explanation." },
                            new
                            {
                                type = "image_url",
                                image_url = new
                                {
                                    url = $"data:image/png;base64,{imageBase64}"
                                }
                            }
                        }
                    }
                },
                max_tokens = 1000
            };

            var payloadJson = JsonSerializer.Serialize(payload);

            // 发送请求
            var request = new HttpRequestMessage(HttpMethod.Post, _apiUrl)
            {
                Content = new StringContent(payloadJson, Encoding.UTF8, "application/json")
            };
            request.Headers.Add("Authorization", $"Bearer {_apiKey}");

            var response = await _httpClient.SendAsync(request, cancellationToken);
            var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return new OcrResult
                {
                    Success = false,
                    ErrorMessage = $"API error: {response.StatusCode} - {responseJson}",
                    Provider = "AI Vision"
                };
            }

            // 解析响应
            var jsonDoc = JsonDocument.Parse(responseJson);
            var root = jsonDoc.RootElement;

            if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
            {
                var message = choices[0].GetProperty("message");
                var content = message.GetProperty("content").GetString() ?? "";

                return new OcrResult
                {
                    Success = true,
                    FullText = content.Trim(),
                    Provider = "AI Vision",
                    TextBlocks = new System.Collections.Generic.List<OcrTextBlock>
                    {
                        new OcrTextBlock
                        {
                            Text = content.Trim(),
                            Confidence = 1.0f
                        }
                    }
                };
            }

            return new OcrResult
            {
                Success = false,
                ErrorMessage = "Invalid response format",
                Provider = "AI Vision"
            };
        }
        catch (Exception ex)
        {
            return new OcrResult
            {
                Success = false,
                ErrorMessage = ex.Message,
                Provider = "AI Vision"
            };
        }
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
