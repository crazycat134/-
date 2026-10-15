using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ScreenReceiver.Server.Services;

/// <summary>
/// Generic OpenAI-compatible vision client. Works with:
///   - DeepSeek:    https://api.deepseek.com/v1
///   - Qwen (百炼): https://dashscope.aliyuncs.com/compatible-mode/v1
///   - 豆包(火山):  https://ark.cn-beijing.volces.com/api/v3
/// Configure via environment variables:
///   LLM_API_KEY   — your API key
///   LLM_MODEL     — model name (e.g. qwen-vl-plus, doubao-seed-vision, deepseek-chat)
///   LLM_BASE_URL  — API base URL (default: https://api.deepseek.com/v1)
/// </summary>
public sealed class LlmClient
{
    private readonly HttpClient _http;
    public string ApiKey { get; }
    public string Model { get; }
    public string BaseUrl { get; }

    public LlmClient(HttpClient http, string apiKey, string model, string baseUrl)
    {
        _http = http;
        ApiKey = apiKey ?? "";
        Model = string.IsNullOrWhiteSpace(model) ? "qwen-vl-plus" : model;
        BaseUrl = string.IsNullOrWhiteSpace(baseUrl)
            ? "https://api.deepseek.com/v1"
            : baseUrl.TrimEnd('/');
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);

    public async Task<string> AnalyzeImageAsync(byte[] jpegBytes, CancellationToken ct = default)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("LLM_API_KEY environment variable is not set.");

        var base64 = Convert.ToBase64String(jpegBytes);
        var dataUrl = $"data:image/jpeg;base64,{base64}";

        var systemPrompt =
"你是银行笔试辅导专家。分析题目截图，直接输出：\n" +
"【答案】[选项字母或结果]\n" +
"【解析】[一句话说清为什么选这个]\n" +
"要求：不要复述题干，不要废话，解析不超过80字；看不清就回\"题目信息缺失\"。";

        var request = new
        {
            model = Model,
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "text", text = "请分析这道题目截图：" },
                        new { type = "image_url", image_url = new { url = dataUrl } }
                    }
                }
            },
            max_tokens = 400,
            temperature = 0.15
        };

        var json = JsonSerializer.Serialize(request);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);
        _http.DefaultRequestHeaders.Accept.Clear();
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var url = $"{BaseUrl}/chat/completions";
        using var response = await _http.PostAsync(url, content, ct);

        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"LLM API {(int)response.StatusCode}: {err}");
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(responseBody);
        var text = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString();
        return string.IsNullOrWhiteSpace(text) ? "(无响应)" : text;
    }
}
