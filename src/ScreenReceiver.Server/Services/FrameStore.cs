using ScreenReceiver.Contracts;

namespace ScreenReceiver.Server.Services;

/// <summary>
/// Thread-safe store holding only the latest frame (no history).
/// </summary>
public sealed class FrameStore
{
    private readonly object _lock = new();
    private byte[]? _imageBytes;
    private FrameMetadata? _metadata;

    public void Update(byte[] imageBytes, FrameMetadata metadata)
    {
        lock (_lock)
        {
            _imageBytes = imageBytes;
            _metadata = metadata;
        }
    }

    public (byte[] bytes, FrameMetadata meta)? GetLatest()
    {
        lock (_lock)
        {
            if (_imageBytes is null || _metadata is null) return null;
            return (_imageBytes.ToArray(), _metadata);
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _imageBytes = null;
            _metadata = null;
        }
    }
}
