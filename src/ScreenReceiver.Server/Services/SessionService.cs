using System.Security.Cryptography;

namespace ScreenReceiver.Server.Services;

/// <summary>
/// Manages the sharing session: token, sharing state, and frame counters.
/// A new token is generated per process launch.
/// </summary>
public sealed class SessionService
{
    public string Token { get; } = GenerateToken();
    public bool IsSharing { get; private set; }
    public long LastFrameId { get; private set; }
    public DateTimeOffset LastFrameAtUtc { get; private set; }
    public DateTimeOffset StartedAtUtc { get; private set; }

    private static string GenerateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(24);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    public bool ValidateToken(string? authorizationHeader)
    {
        if (string.IsNullOrWhiteSpace(authorizationHeader)) return false;
        var expected = $"Bearer {Token}";
        return string.Equals(authorizationHeader, expected, StringComparison.Ordinal);
    }

    public void StartSharing()
    {
        IsSharing = true;
        StartedAtUtc = DateTimeOffset.UtcNow;
        LastFrameId = 0;
    }

    public void StopSharing()
    {
        IsSharing = false;
    }

    public void OnFrameReceived(long frameId)
    {
        LastFrameId = frameId;
        LastFrameAtUtc = DateTimeOffset.UtcNow;
    }
}
