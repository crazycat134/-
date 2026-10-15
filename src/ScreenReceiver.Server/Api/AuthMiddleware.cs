using ScreenReceiver.Contracts;
using ScreenReceiver.Server.Services;

namespace ScreenReceiver.Server.Api;

/// <summary>
/// Bearer-token auth middleware.
/// Static files and /api/v1/health are public; everything under /api requires the token.
/// </summary>
public static class AuthMiddleware
{
    public static IApplicationBuilder UseSessionAuth(this IApplicationBuilder app)
    {
        return app.Use(async (context, next) =>
        {
            var path = context.Request.Path;

            // Allow static files (index.html, app.js, styles.css) without auth.
            // The iPhone page itself is loaded first, then JS uses the token from the URL.
            if (!path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
            {
                await next();
                return;
            }

            if (path.StartsWithSegments("/api/v1/health", StringComparison.OrdinalIgnoreCase))
            {
                await next();
                return;
            }

            var session = context.RequestServices.GetRequiredService<SessionService>();
            if (!session.ValidateToken(context.Request.Headers.Authorization))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(
                    new ApiErrorEnvelope(new ApiError(ErrorCodes.Unauthorized, "Missing or invalid Bearer token.")));
                return;
            }

            await next();
        });
    }
}
