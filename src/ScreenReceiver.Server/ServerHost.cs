using System.IO;
using ScreenReceiver.Server.Api;
using ScreenReceiver.Server.Services;

namespace ScreenReceiver.Server;

/// <summary>
/// Builds the WebApplication. Called by both the standalone Program.cs
/// and the WPF Desktop host (so they share the same DI and endpoint wiring).
/// </summary>
public static class ServerHost
{
    public static WebApplication Build(string[] args, string hostUrl, SessionService? session = null)
    {
        // Specify ContentRoot and WebRoot up front via WebApplicationOptions.
        // Calling UseContentRoot/UseWebRoot AFTER CreateBuilder triggers
        // "The content root/web root changed" in ASP.NET Core 8+.
        var webRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        var options = new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
            WebRootPath = Directory.Exists(webRootPath) ? webRootPath : null
        };
        var builder = WebApplication.CreateBuilder(options);
        builder.WebHost.UseUrls(hostUrl);

        builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o =>
        {
            o.MultipartBodyLengthLimit = 4 * 1024 * 1024;
        });

        var sessionService = session ?? new SessionService();
        builder.Services.AddSingleton(sessionService);
        builder.Services.AddSingleton<FrameStore>();

        // OpenAI-compatible vision model config.
        // Default: DeepSeek vision model (deepseek-flash).
        // Switch providers by changing these env vars, no code change needed.
        var apiKey = Environment.GetEnvironmentVariable("LLM_API_KEY")
                  ?? Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY") ?? "";
        var model = Environment.GetEnvironmentVariable("LLM_MODEL")
                 ?? Environment.GetEnvironmentVariable("DEEPSEEK_MODEL") ?? "deepseek-flash";
        var baseUrl = Environment.GetEnvironmentVariable("LLM_BASE_URL")
                   ?? "https://api.deepseek.com/v1";
        builder.Services.AddSingleton(new LlmClient(new HttpClient(), apiKey, model, baseUrl));

        // Interview assistant: Whisper (ASR) + LLM (hint generation)
        var whisperKey = Environment.GetEnvironmentVariable("WHISPER_API_KEY") ?? apiKey;
        var whisperBase = Environment.GetEnvironmentVariable("WHISPER_BASE_URL")
                       ?? "https://api.openai.com/v1";
        var whisperModel = Environment.GetEnvironmentVariable("WHISPER_MODEL") ?? "whisper-1";
        builder.Services.AddSingleton(new InterviewAssistantService(
            new HttpClient(), apiKey, model, baseUrl, whisperKey, whisperBase, whisperModel));

        var app = builder.Build();

        // UseDefaultFiles must come BEFORE UseStaticFiles so "/" serves index.html
        app.UseDefaultFiles();
        app.UseStaticFiles();
        app.UseSessionAuth();
        app.MapFrameEndpoints();

        // Interview endpoints
        app.MapPost("/api/v1/interview/transcribe", async (HttpRequest request, InterviewAssistantService svc) =>
        {
            if (!svc.IsConfigured)
                return Results.Json(new { error = "未配置 WHISPER_API_KEY 或 LLM_API_KEY" }, statusCode: 500);
            var form = await request.ReadFormAsync();
            var file = form.Files.GetFile("audio");
            if (file is null)
                return Results.Json(new { error = "missing audio file" }, statusCode: 400);
            using var stream = file.OpenReadStream();
            var result = await svc.ProcessAudioAsync(stream, file.ContentType, file.FileName);
            return Results.Json(result);
        });

        app.MapPost("/api/v1/interview/reset", (InterviewAssistantService svc) =>
        {
            svc.Reset();
            return Results.Ok(new { reset = true });
        });

        return app;
    }
}
