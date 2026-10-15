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
"あなたは日本のIT企業の面接コーチです。面接官の質問に対して、その場で読み上げられる完全な日本語の回答スクリプトを提供してください。\n" +
"候補者の背景：大阪大学大学院、家電量販店アルバイト（会員登録当日店舗No.1）、中古EC運営、独学でAPI生成AIを使った出品支援システム開発。\n" +
"以下の形式で出力してください：\n" +
"🎯 考察ポイント：[面接官が何を見ているか]\n" +
"🗣️ 回答スクリプト（です・ます調、100〜150文字程度）：\n" +
"[冒頭の挨拶→具体的なエピソード（STAR法）→まとめ]\n" +
"💡 キーワード：[中国語で3つの要点]\n" +
"⚠️ 注意：[失敗しやすいポイント]\n" +
"日本語のスクリプトは必ず完全な文で出力し、キーワードだけで終わらないでください。";

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

        if (!IsConfigured)
        {
            Console.WriteLine($"[Interview] Not configured: llmKey={!string.IsNullOrEmpty(_llmApiKey)}, whisperKey={!string.IsNullOrEmpty(_whisperApiKey)}");
            return new InterviewResult("", "", "未配置WHISPER_API_KEY");
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        Console.WriteLine($"[Interview] Chunk received ({wavBytes.Length} bytes)");

        string transcribed;
        try { transcribed = await TranscribeBytesAsync(wavBytes, "audio/wav", "chunk.wav", ct); }
        catch (Exception ex)
        {
            Console.WriteLine($"[Interview] Whisper error after {sw.ElapsedMilliseconds}ms: {ex.Message}");
            return new InterviewResult("", "", $"转写失败: {ex.Message}");
        }
        var whisperMs = sw.ElapsedMilliseconds;
        Console.WriteLine($"[Interview] Whisper done in {whisperMs}ms, text='{transcribed?.Substring(0, Math.Min(30, transcribed?.Length ?? 0))}...'");

        if (string.IsNullOrWhiteSpace(transcribed) || transcribed.Trim().Length < 3)
        {
            Console.WriteLine($"[Interview] Too short, skip. Total {sw.ElapsedMilliseconds}ms");
            return new InterviewResult("", "", "");
        }

        transcribed = transcribed.Trim();
        lock (_lock)
        {
            _history.Add(new { role = "user", content = transcribed });
            while (_history.Count > 20) _history.RemoveAt(0);
        }

        string hint;
        var llmSw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            hint = await GenerateHintAsync(transcribed, ct);
            llmSw.Stop();
            lock (_lock) { _history.Add(new { role = "assistant", content = hint }); }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Interview] LLM error after {sw.ElapsedMilliseconds}ms: {ex.Message}");
            return new InterviewResult(transcribed, "", $"AI失败: {ex.Message}");
        }

        _lastCall = DateTime.UtcNow;
        lock (_lock) { _latestResult = new InterviewResult(transcribed, hint, "ok"); }
        Console.WriteLine($"[Interview] TOTAL {sw.ElapsedMilliseconds}ms (Whisper={whisperMs}ms LLM={llmSw.ElapsedMilliseconds}ms) | Q: {transcribed.Substring(0, Math.Min(20, transcribed.Length))}...");
        return new InterviewResult(transcribed, hint, "ok");
    }

    private InterviewResult? _latestResult;
    private int _latestVolume;

    /// <summary>Called by audio capture to update current mic RMS volume (0-32767).</summary>
    public void SetVolume(int rms) => _latestVolume = rms;

    public InterviewResult GetLatest()
    {
        lock (_lock)
        {
            var r = _latestResult ?? new InterviewResult("", "", "");
            return r with { Volume = _latestVolume };
        }
    }

    private async Task<string> TranscribeBytesAsync(byte[] audioBytes, string contentType, string fileName, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(audioBytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(fileContent, "file", fileName);
        form.Add(new StringContent(_whisperModel), "model");
        // Let Whisper auto-detect language (ja/zh/en mixed in interview)
        // form.Add(new StringContent("ja"), "language");

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

    private long _questionId;

    private async Task<string> GenerateHintAsync(string transcribed, CancellationToken ct)
    {
        var qid = Interlocked.Increment(ref _questionId);

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
            max_tokens = 400,
            temperature = 0.3,
            stream = true
        };

        using var content = new StringContent(
            JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_llmBaseUrl}/chat/completions")
        {
            Content = content
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _llmApiKey);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(cts.Token);
            throw new InvalidOperationException($"LLM {(int)resp.StatusCode}: {err}");
        }

        var sb = new StringBuilder();
        using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync(cts.Token);
            if (string.IsNullOrEmpty(line) || !line.StartsWith("data: ")) continue;
            var data = line[6..];
            if (data == "[DONE]") break;
            try
            {
                using var doc = JsonDocument.Parse(data);
                var delta = doc.RootElement.GetProperty("choices")[0]
                    .GetProperty("delta").GetProperty("content").GetString();
                if (!string.IsNullOrEmpty(delta))
                {
                    sb.Append(delta);
                    lock (_lock)
                    {
                        _latestResult = new InterviewResult(transcribed, sb.ToString(), $"streaming q{qid}");
                    }
                }
            }
            catch { }
        }

        return sb.ToString();
    }
}

public record InterviewResult(string Transcribed, string Hint, string Status, int Volume)
{
    public InterviewResult(string t, string h, string s) : this(t, h, s, 0) { }
}
