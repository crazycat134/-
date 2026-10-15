using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ScreenReceiver.Server.Services;

/// <summary>
/// Interview assistant: audio transcription (Whisper) + LLM hint generation.
/// Maintains conversation history so follow-up questions get context.
/// </summary>
public sealed class InterviewAssistantService
{
    private readonly HttpClient _http;
    private readonly string _llmApiKey;
    private readonly string _llmModel;
    private readonly string _llmBaseUrl;
    private readonly string _whisperApiKey;
    private readonly string _whisperBaseUrl;
    private readonly string _whisperModel;

    private readonly object _lock = new();
    private readonly List<object> _history = new();
    private DateTime _lastCall = DateTime.MinValue;
    public bool Muted { get; set; }

    private const string InterviewSystemPrompt =
"你是顶级面试辅导教练，通过手机给候选人实时提示。\n" +
"规则：\n" +
"1. 极短：50字以内，只给关键词和话术框架\n" +
"2. 应对追问：结合上一轮对话递进回答\n" +
"3. 不要寒暄，直接输出\n" +
"格式：\n" +
"💡 [核心观点/技术名词]\n" +
"🗣️ [1-2句话术提示]\n" +
"如果候选人没在说话或面试官在闲聊，输出\"\"（空）。";

    public InterviewAssistantService(
        HttpClient http,
        string llmApiKey, string llmModel, string llmBaseUrl,
        string whisperApiKey, string whisperBaseUrl, string whisperModel)
    {
        _http = http;
        _llmApiKey = llmApiKey;
        _llmModel = llmModel;
        _llmBaseUrl = llmBaseUrl;
        _whisperApiKey = whisperApiKey;
        _whisperBaseUrl = whisperBaseUrl;
        _whisperModel = whisperModel;
    }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_llmApiKey) && !string.IsNullOrWhiteSpace(_whisperApiKey);

    /// <summary>
    /// Reset conversation history (start of a new interview).
    /// </summary>
    public void Reset()
    {
        lock (_lock) { _history.Clear(); }
    }

    /// <summary>
    /// Transcribe audio via Whisper, then generate interview hint via LLM.
    /// Called from the phone (uploads audio chunk).
    /// </summary>
    public async Task<InterviewResult> ProcessAudioAsync(Stream audioStream, string contentType, string fileName, CancellationToken ct = default)
    {
        var ms = new MemoryStream();
        await audioStream.CopyToAsync(ms, ct);
        return await ProcessWavChunkAsync(ms.ToArray(), ct);
    }

    /// <summary>
    /// Process a raw WAV chunk directly (from desktop WASAPI loopback).
    /// Skips the phone upload step.
    /// </summary>
    public async Task<InterviewResult> ProcessWavChunkAsync(byte[] wavBytes, CancellationToken ct = default)
    {
        if ((DateTime.UtcNow - _lastCall) < TimeSpan.FromSeconds(2))
            return new InterviewResult("", "", "debounced");

        string transcribed;
        try { transcribed = await TranscribeBytesAsync(wavBytes, "audio/wav", "chunk.wav", ct); }
        catch (Exception ex) { return new InterviewResult("", "", $"转写失败: {ex.Message}"); }

        if (string.IsNullOrWhiteSpace(transcribed) || transcribed.Trim().Length < 3)
            return new InterviewResult("", "", "");

        transcribed = transcribed.Trim();
        Console.WriteLine($"[Interview] 面试官: {transcribed}");

        lock (_lock)
        {
            _history.Add(new { role = "user", content = transcribed });
            while (_history.Count > 20) _history.RemoveAt(0);
        }

        string hint;
        try
        {
            hint = await GenerateHintAsync(ct);
            lock (_lock) { _history.Add(new { role = "assistant", content = hint }); }
        }
        catch (Exception ex) { return new InterviewResult(transcribed, "", $"AI失败: {ex.Message}"); }

        _lastCall = DateTime.UtcNow;
        lock (_lock)
        {
            _latestResult = new InterviewResult(transcribed, hint, "ok");
        }
        Console.WriteLine($"[Interview] 提示: {hint}");
        return _latestResult;
    }

    private InterviewResult? _latestResult;

    public InterviewResult GetLatest()
    {
        lock (_lock) { return _latestResult ?? new InterviewResult("", "", ""); }
    }

    private async Task<string> TranscribeBytesAsync(byte[] audioBytes, string contentType, string fileName, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(audioBytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(fileContent, "file", fileName);
        form.Add(new StringContent(_whisperModel), "model");
        form.Add(new StringContent("zh"), "language");

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_whisperBaseUrl}/audio/transcriptions")
        {
            Content = form
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _whisperApiKey);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        using var resp = await _http.SendAsync(req, cts.Token);
        var body = await resp.Content.ReadAsStringAsync(cts.Token);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Whisper {(int)resp.StatusCode}: {body}");

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
    }

    private async Task<string> GenerateHintAsync(CancellationToken ct)
    {
        List<object> messages;
        lock (_lock)
        {
            messages = new List<object> { new { role = "system", content = InterviewSystemPrompt } };
            messages.AddRange(_history);
        }

        var request = new
        {
            model = _llmModel,
            messages,
            max_tokens = 200,
            temperature = 0.3
        };

        using var content = new StringContent(
            JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_llmBaseUrl}/chat/completions")
        {
            Content = content
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _llmApiKey);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        using var resp = await _http.SendAsync(req, cts.Token);
        var body = await resp.Content.ReadAsStringAsync(cts.Token);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"LLM {(int)resp.StatusCode}: {body}");

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("choices")[0]
            .GetProperty("message").GetProperty("content").GetString() ?? "";
    }
}

public record InterviewResult(string Transcribed, string Hint, string Status);
