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
"你是面试辅导教练，候选人正在日本IT公司面试。面试官说日语，你用中文给提示。\n" +
"候选人背景：大阪大学硕士（2026年3月毕业），专业基础一般但长期规划执行力强；日本家电量贩店兼职（会员注册当日门店第一）；自营二手商品EC；自学API和生成式AI做出品辅助系统。\n\n" +
"8类故事核心素材：\n" +
"1.主动性：备考大学院时发现过去问缺详解，主动组6人学习小组，最终全员考上\n" +
"2.沟通：家电量贩店接待顾客，根据顾客关注点调整介绍方式，会员注册当日门店第一\n" +
"3.学习能力：二手EC业务中自学API+生成式AI，把商品信息整理/价格参考/文案生成连成流程，主要功能已跑通（不夸大量化成果）\n" +
"4.创新：不只用AI写文案，而是把信息获取/商品检查/价格判断/出品准备连成完整流程，保留人工确认\n" +
"5.问题分析：二手商品定价，综合历史成交价/商品状态/上架后关注度调整，无系统统计但有复盘意识\n" +
"6.执行力/多任务：从薄弱基础出发，兼顾打工同时备考英语/数学/专业/研究计划，每天读论文，最终考入阪大并完成硕士\n" +
"7.失败反思：日语小组讨论时因紧张+没提前整理思路，无法及时发言；改进=提前写观点、结论先行、注意倾听找补充时机\n" +
"8.责任心：长期目标下分类任务、制定计划、持续推进直到完成\n\n" +
"规则：\n" +
"- 面试官说完后，判断他在问哪类问题（主动性/沟通/学习/创新/分析/执行力/失败/责任心）\n" +
"- 从对应故事提取2-3个关键词和1句话术\n" +
"- 极短：50字以内\n" +
"- 如果面试官在追问细节，结合上一轮对话递进回答\n" +
"- 不要寒暄，直接输出\n\n" +
"格式：\n" +
"💡 [用哪个故事 + 核心关键词]\n" +
"🗣️ [中文话术提示，候选人可以直接说的要点]\n\n" +
"如果没听清或问题模糊，输出\"\"。";

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
