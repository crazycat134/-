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
        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.UseUrls(hostUrl);

        // Point web root directly at the exe directory's wwwroot (copied by the
        // Desktop csproj). Do NOT call UseContentRoot here — the .NET SDK already
        // sets it during `dotnet run`, and changing it again triggers
        // "The content root/web root changed" exceptions in ASP.NET Core 8+.
        builder.WebHost.UseWebRoot(Path.Combine(AppContext.BaseDirectory, "wwwroot"));

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

        var app = builder.Build();

        app.UseStaticFiles();
        app.UseSessionAuth();
        app.MapFrameEndpoints();

        return app;
    }
}
