using System;
using System.Drawing;
using System.Windows.Forms;

namespace ScreenReceiver.Desktop.Capture;

/// <summary>
/// Primary-screen capture via Graphics.CopyFromScreen.
/// Good enough for the first prototype; swap for Windows.Graphics.Capture
/// when per-window selection is needed.
/// </summary>
public sealed class ScreenCaptureService : IScreenCaptureService
{
    public Bitmap? CaptureScreen()
    {
        try
        {
            var screen = Screen.PrimaryScreen;
            if (screen is null) return null;
            var bounds = screen.Bounds;
            var bmp = new Bitmap(bounds.Width, bounds.Height);
            using var g = Graphics.FromImage(bmp);
            g.CopyFromScreen(bounds.X, bounds.Y, 0, 0, bounds.Size);
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose() { }
}
