using ScreenReceiver.Contracts;

namespace ScreenReceiver.Server.Services;

/// <summary>
/// Thread-safe ring buffer holding the most recent N frames (default 20).
/// Enables stitching multiple frames into a long image for multi-screen questions.
/// </summary>
public sealed class FrameStore
{
    private const int Capacity = 20;
    private readonly object _lock = new();
    private readonly List<(byte[] bytes, FrameMetadata meta)> _buffer = new(Capacity);

    public void Update(byte[] imageBytes, FrameMetadata metadata)
    {
        lock (_lock)
        {
            _buffer.Add((imageBytes, metadata));
            while (_buffer.Count > Capacity) _buffer.RemoveAt(0);
        }
    }

    public (byte[] bytes, FrameMetadata meta)? GetLatest()
    {
        lock (_lock)
        {
            if (_buffer.Count == 0) return null;
            var (bytes, meta) = _buffer[^1];
            return (bytes.ToArray(), meta);
        }
    }

    /// <summary>Returns the most recent n frames (oldest first), up to Capacity.</summary>
    public List<(byte[] bytes, FrameMetadata meta)> GetRecent(int n)
    {
        lock (_lock)
        {
            var take = Math.Min(n, _buffer.Count);
            return _buffer[^take..]
                .Select(f => (f.bytes.ToArray(), f.meta))
                .ToList();
        }
    }

    public void Clear()
    {
        lock (_lock) { _buffer.Clear(); }
    }
}
