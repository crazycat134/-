using Microsoft.AspNetCore.Mvc;
using ScreenReceiver.Contracts;
using ScreenReceiver.Server.Services;

namespace ScreenReceiver.Server.Api;

/// <summary>
/// Minimal API endpoints under /api/v1.
/// </summary>
public static class FrameEndpoints
{
    private const long MaxFrameBytes = 2 * 1024 * 1024; // 2 MB

    public static void MapFrameEndpoints(this IEndpointRouteBuilder app)
    {
        var v1 = app.MapGroup("/api/v1");

        // GET /api/v1/health — public, used for connectivity checks.
        v1.MapGet("/health", () => Results.Ok(new
        {
            status = "ok",
            utcNow = DateTimeOffset.UtcNow
        }));

        // GET /api/v1/session
        v1.MapGet("/session", (SessionService session) => Results.Ok(new
        {
            sharing = session.IsSharing,
            frameId = session.LastFrameId,
            lastFrameAtUtc = session.LastFrameAtUtc,
            startedAtUtc = session.StartedAtUtc
        }));

        // POST /api/v1/session/start
        v1.MapPost("/session/start", (SessionService session) =>
        {
            if (session.IsSharing)
                return Results.Conflict(new ApiErrorEnvelope(new ApiError(
                    ErrorCodes.InvalidSessionState, "Session already started.")));
            session.StartSharing();
            return Results.Ok(new { sharing = true });
        });

        // POST /api/v1/session/stop
        v1.MapPost("/session/stop", (SessionService session, FrameStore store) =>
        {
            session.StopSharing();
            store.Clear();
            return Results.Ok(new { sharing = false });
        });

        // POST /api/v1/frames — upload the latest JPEG frame.
        v1.MapPost("/frames", async (HttpRequest request, SessionService session, FrameStore store) =>
        {
            if (!session.IsSharing)
                return Results.Json(
                    new ApiErrorEnvelope(new ApiError(ErrorCodes.SessionStopped, "Session is not active.")),
                    statusCode: StatusCodes.Status410Gone);

            if (!long.TryParse(request.Headers["X-Frame-Id"].FirstOrDefault(), out var frameId) || frameId <= 0)
                return Results.Json(
                    new ApiErrorEnvelope(new ApiError(ErrorCodes.InvalidFrame, "Missing or invalid X-Frame-Id header.")),
                    statusCode: StatusCodes.Status400BadRequest);

            using var ms = new MemoryStream();
            await request.Body.CopyToAsync(ms);
            var bytes = ms.ToArray();

            if (bytes.Length == 0)
                return Results.Json(
                    new ApiErrorEnvelope(new ApiError(ErrorCodes.InvalidFrame, "Empty body.")),
                    statusCode: StatusCodes.Status400BadRequest);

            if (bytes.Length > MaxFrameBytes)
                return Results.Json(
                    new ApiErrorEnvelope(new ApiError(ErrorCodes.FrameTooLarge, "Frame exceeds 2 MB limit.")),
                    statusCode: StatusCodes.Status413PayloadTooLarge);

            var contentType = request.Headers.ContentType.ToString();
            if (!contentType.StartsWith("image/jpeg", StringComparison.OrdinalIgnoreCase))
                return Results.Json(
                    new ApiErrorEnvelope(new ApiError(ErrorCodes.UnsupportedMediaType, "Only image/jpeg is accepted.")),
                    statusCode: StatusCodes.Status415UnsupportedMediaType);

            var meta = new FrameMetadata(
                FrameId: frameId,
                CapturedAtUtc: DateTimeOffset.UtcNow,
                Width: 0,
                Height: 0,
                ContentType: "image/jpeg",
                SizeBytes: bytes.Length);

            store.Update(bytes, meta);
            session.OnFrameReceived(frameId);

            return Results.Json(new
            {
                accepted = true,
                frameId,
                receivedAtUtc = meta.CapturedAtUtc
            });
        });

        // GET /api/v1/frames/latest — returns raw JPEG bytes.
        v1.MapGet("/frames/latest", (FrameStore store) =>
        {
            var latest = store.GetLatest();
            if (latest is null)
                return Results.Json(
                    new ApiErrorEnvelope(new ApiError(ErrorCodes.FrameNotAvailable, "No frame available.")),
                    statusCode: StatusCodes.Status404NotFound);

            var (bytes, meta) = latest.Value;
            return Results.File(bytes, "image/jpeg", enableRangeProcessing: false);
        });

        // GET /api/v1/frames/latest/meta
        v1.MapGet("/frames/latest/meta", (FrameStore store) =>
        {
            var latest = store.GetLatest();
            return latest is null
                ? Results.NotFound(new ApiErrorEnvelope(new ApiError(ErrorCodes.FrameNotAvailable, "No frame available.")))
                : Results.Ok(latest.Value.meta);
        });

        // POST /api/v1/analyze — stitch recent frames, send to vision LLM, return answer.
        v1.MapPost("/analyze", async (FrameStore store, LlmClient llm) =>
        {
            if (!llm.IsConfigured)
                return Results.Json(
                    new ApiErrorEnvelope(new ApiError("NOT_CONFIGURED",
                        "LLM_API_KEY environment variable is not set on the Windows host.")),
                    statusCode: StatusCodes.Status500InternalServerError);

            var frames = store.GetRecent(3);
            Console.WriteLine($"[Analyze] Request received, frames={frames.Count}");
            if (frames.Count == 0)
                return Results.Json(
                    new ApiErrorEnvelope(new ApiError(ErrorCodes.FrameNotAvailable, "No frame available. Start sharing first.")),
                    statusCode: StatusCodes.Status404NotFound);

            try
            {
                var jpegs = frames.Select(f => f.bytes).ToList();
                var stitched = ImageStitcher.StitchVertical(jpegs);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var answer = await llm.AnalyzeImageAsync(stitched);
                sw.Stop();
                var last = frames[frames.Count - 1];

                // Log to file
                var logEntry = System.Text.Json.JsonSerializer.Serialize(new
                {
                    time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    frameId = last.meta.FrameId,
                    elapsedMs = sw.ElapsedMilliseconds,
                    answer
                });
                System.IO.File.AppendAllText("analyze_history.jsonl", logEntry + "\n");
                Console.WriteLine($"[Analyze] Done in {sw.ElapsedMilliseconds}ms, frame={last.meta.FrameId}");

                return Results.Ok(new { answer, frameCount = frames.Count, frameId = last.meta.FrameId });
            }
            catch (Exception ex)
            {
                return Results.Json(
                    new ApiErrorEnvelope(new ApiError("ANALYSIS_FAILED", ex.Message)),
                    statusCode: StatusCodes.Status502BadGateway);
            }
        });

        // GET /api/v1/llm/status — whether API key is configured.
        v1.MapGet("/llm/status", (LlmClient llm) =>
            Results.Ok(new { configured = llm.IsConfigured, model = llm.Model, baseUrl = llm.BaseUrl }));
    }
}
