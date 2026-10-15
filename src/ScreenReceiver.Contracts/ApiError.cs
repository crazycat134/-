namespace ScreenReceiver.Contracts;

/// <summary>
/// Uniform error envelope returned by all /api/v1 endpoints on failure.
/// </summary>
public sealed record ApiError(string Code, string Message);

public sealed record ApiErrorEnvelope(ApiError Error);

public static class ErrorCodes
{
    public const string InvalidFrame = "INVALID_FRAME";
    public const string Unauthorized = "UNAUTHORIZED";
    public const string FrameNotAvailable = "FRAME_NOT_AVAILABLE";
    public const string InvalidSessionState = "INVALID_SESSION_STATE";
    public const string SessionStopped = "SESSION_STOPPED";
    public const string FrameTooLarge = "FRAME_TOO_LARGE";
    public const string UnsupportedMediaType = "UNSUPPORTED_MEDIA_TYPE";
    public const string RateLimited = "RATE_LIMITED";
}
