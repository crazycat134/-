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

        // All LLM via DeepSeek (no OpenAI)
        var deepseekKey = Environment.GetEnvironmentVariable("LLM_API_KEY")
                       ?? Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY") ?? "";
        var deepseekBase = "https://api.deepseek.com/v1";

        // Question recognition: deepseek-flash (vision, supports images)
        var visionModel = Environment.GetEnvironmentVariable("VISION_MODEL") ?? "deepseek-flash";
        builder.Services.AddSingleton(new LlmClient(new HttpClient(), deepseekKey, visionModel, deepseekBase));

        // Interview assistant LLM: deepseek-reasoner (top tier reasoning)
        var llmModel = Environment.GetEnvironmentVariable("LLM_MODEL") ?? "deepseek-reasoner";

        var app = builder.Build();

        // UseDefaultFiles must come BEFORE UseStaticFiles so "/" serves index.html
        app.UseDefaultFiles();
        app.UseStaticFiles();
        app.UseSessionAuth();
        app.MapFrameEndpoints();

        return app;
    }
}
