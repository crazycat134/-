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
    private readonly SemaphoreSlim _sem = new(1, 1); // serialize LLM calls to avoid 429
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

        await _sem.WaitAsync(ct);
        try
        {
            return await AnalyzeImageInternalAsync(jpegBytes, ct);
        }
        finally
        {
            _sem.Release();
        }
    }

    private async Task<string> AnalyzeImageInternalAsync(byte[] jpegBytes, CancellationToken ct)
    {

        var base64 = Convert.ToBase64String(jpegBytes);
        var dataUrl = $"data:image/jpeg;base64,{base64}";

        var systemPrompt = QuestionRouterService.Route(jpegBytes);

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
        var url = $"{BaseUrl}/chat/completions";

        // Per-request message handler to avoid thread-unsafe DefaultRequestHeaders under concurrency.
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Content = content;
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, ct);

        try
        {
            using var response = await _http.SendAsync(req, linked.Token);

            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync(linked.Token);
                Console.WriteLine($"[LLM] HTTP {(int)response.StatusCode}: {err}");
                throw new InvalidOperationException($"LLM API {(int)response.StatusCode}: {err}");
            }

            var responseBody = await response.Content.ReadAsStringAsync(linked.Token);
            Console.WriteLine($"[LLM] OK, {responseBody.Length} bytes");
            using var doc = JsonDocument.Parse(responseBody);
            var text = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();
            return string.IsNullOrWhiteSpace(text) ? "(无响应)" : text;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            Console.WriteLine("[LLM] Timeout after 30s");
            throw new InvalidOperationException("LLM 请求超时（30秒），请重试或检查网络");
        }
    }
}
