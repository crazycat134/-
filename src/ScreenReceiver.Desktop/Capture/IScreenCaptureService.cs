using System.Drawing;

namespace ScreenReceiver.Desktop.Capture;

/// <summary>
/// Abstraction over screen capture. The first implementation uses
/// Graphics.CopyFromScreen (simplest path). Later this can be swapped
/// for Windows.Graphics.Capture (window picker, GPU frames) without
/// touching the upload or UI layers.
/// </summary>
public interface IScreenCaptureService : IDisposable
{
    /// <summary>
    /// Returns a new Bitmap of the captured screen, or null on failure.
    /// Caller owns the returned bitmap and must Dispose it.
    /// </summary>
    Bitmap? CaptureScreen();
}
